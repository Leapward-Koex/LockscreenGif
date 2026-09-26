using System.Net.Http.Headers;
using System.Text.Json;

namespace LockscreenGif.Services.Analytics;

/// <summary>Best-effort analytics gated by the saved sharing preference. Events only live in a small in-memory queue.</summary>
public sealed class AnalyticsService : IDisposable
{
    private const int QueueCapacity = 64;
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromMilliseconds(1500);
    private readonly object _sync = new();
    private readonly object _preferencesSync = new();
    private readonly Queue<QueuedEvent> _pending = new();
    private readonly SemaphoreSlim _available = new(0, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly bool _allowSending;
    private readonly TimeProvider _timeProvider;
    private readonly Func<AnalyticsWindowsContext> _windowsContext;
    private readonly string _preferencesPath;
    private readonly string _projectToken;
    private readonly string? _environment;
    private readonly string _appVersion;
    private readonly string _windowsVersion;
    private readonly Uri? _endpoint;
    private readonly Task _worker;
    private CancellationTokenSource _consent = new();
    private string? _installationId;
    private string? _sessionId;
    private DateTimeOffset _sessionStarted;
    private DateTimeOffset _lastActivity;
    private volatile bool _enabled;
    private volatile bool _hasSavedPreference;
    private volatile bool _stopping;
    private long _generation;
    private int _stopRequested;
    private Task? _shutdown;
    private int _disposed;

    public AnalyticsService(
        AnalyticsOptions options,
        string preferencesPath,
        string appVersion,
        string windowsVersion,
        bool allowSending = true,
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null,
        Func<AnalyticsWindowsContext>? windowsContext = null
    )
    {
        _preferencesPath = preferencesPath;
        _projectToken = options.ProjectToken?.Trim() ?? "";
        _environment = options.Environment switch
        {
            AnalyticsEnvironment.Development => "development",
            AnalyticsEnvironment.Production => "production",
            _ => null,
        };
        _endpoint = GetEndpoint(options.Host);
        _appVersion = SafeVersion(appVersion);
        _windowsVersion = SafeVersion(windowsVersion);
        _allowSending = allowSending;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _windowsContext = windowsContext ?? WindowsAnalyticsContextCollector.Collect;
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        LoadPreferences();
        _worker = Task.Run(DeliverAsync);
    }

    public bool IsEnabled => _enabled;

    public bool HasSavedPreference => _hasSavedPreference;

    public bool IsConfigured =>
        _endpoint is not null
        && _environment is not null
        && _projectToken.StartsWith("phc_", StringComparison.Ordinal)
        && _projectToken.Length > 4;

    public bool SetEnabled(bool enabled)
    {
        // Serialize preference writes separately: neither disk I/O nor network callbacks may hold the capture lock.
        lock (_preferencesSync)
        {
            if (_stopping)
            {
                return false;
            }

            string? installationId;
            CancellationTokenSource? cancelledConsent = null;
            lock (_sync)
            {
                installationId = enabled ? _installationId ?? Guid.NewGuid().ToString("D") : null;
                if (!enabled)
                {
                    cancelledConsent = DisableCollection();
                }
            }

            RequestCancellation(cancelledConsent, disposeWhenDone: true);
            var saved = SavePreferences(new Preferences(1, enabled, installationId));
            cancelledConsent = null;
            lock (_sync)
            {
                _hasSavedPreference = true;
                if (enabled && saved && !_stopping)
                {
                    _installationId = installationId;
                    _enabled = true;
                }
                else if (enabled && !saved)
                {
                    cancelledConsent = DisableCollection();
                }
            }

            RequestCancellation(cancelledConsent, disposeWhenDone: true);
            return saved;
        }
    }

    public void Track(AnalyticsEvent eventName, AnalyticsProperties? properties = null)
    {
        var lockTaken = false;
        try
        {
            if (_stopping || !_enabled || !_allowSending || !IsConfigured)
            {
                return;
            }

            var name = EventName(eventName);
            if (name is null)
            {
                return;
            }

            // Losing an event is preferable to delaying a user action, including under concurrent capture.
            Monitor.TryEnter(_sync, ref lockTaken);
            if (!lockTaken || _stopping || !_enabled || _pending.Count >= QueueCapacity)
            {
                return;
            }

            var timestamp = _timeProvider.GetUtcNow();
            EnsureSession(timestamp);
            _pending.Enqueue(
                new QueuedEvent(
                    name,
                    properties,
                    _installationId!,
                    _sessionId!,
                    timestamp,
                    Guid.CreateVersion7(timestamp),
                    Interlocked.Read(ref _generation),
                    _consent.Token
                )
            );
            SignalWorker();
        }
        catch
        {
            // Telemetry must never interfere with the operation being measured.
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(_sync);
            }
        }
    }

    public Task ShutdownAsync()
    {
        lock (_sync)
        {
            _stopping = true;
            SignalWorker();
            return _shutdown ??= DrainAsync();
        }
    }

    /// <summary>Immediately stops accepting events; never waits for HTTP, cancellation callbacks, or queue draining.</summary>
    public void Stop()
    {
        _stopping = true;
        if (Interlocked.Exchange(ref _stopRequested, 1) != 0)
        {
            return;
        }

        Interlocked.Increment(ref _generation);
        var lockTaken = false;
        try
        {
            Monitor.TryEnter(_sync, ref lockTaken);
            if (lockTaken)
            {
                _pending.Clear();
            }
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(_sync);
            }
        }

        RequestCancellation(_lifetime);
        SignalWorker();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Stop();
        if (_ownsHttpClient)
        {
            // HttpClient.Dispose can synchronously run transport cancellation callbacks. Keep it off the caller's thread too.
            _ = Task.Run(() =>
            {
                try
                {
                    _httpClient.Dispose();
                }
                catch { }
            });
        }
    }

    private async Task DrainAsync()
    {
        try
        {
            await _worker.WaitAsync(ShutdownTimeout).ConfigureAwait(false);
        }
        catch
        {
            Stop();
        }
    }

    private async Task DeliverAsync()
    {
        try
        {
            while (true)
            {
                await _available.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                while (true)
                {
                    QueuedEvent next;
                    lock (_sync)
                    {
                        if (Volatile.Read(ref _stopRequested) != 0)
                        {
                            return;
                        }

                        if (!_pending.TryDequeue(out next!))
                        {
                            if (_stopping)
                            {
                                return;
                            }

                            break;
                        }
                    }

                    // Even a transport that blocks before returning its Task cannot hold a lock needed by application code.
                    await SendAsync(next).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // Cancellation and transport failures are deliberately silent.
        }
        finally
        {
            lock (_sync)
            {
                _pending.Clear();
            }
        }
    }

    private async Task SendAsync(QueuedEvent item)
    {
        try
        {
            if (!CanDeliver(item))
            {
                return;
            }

            var payload = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    api_key = _projectToken,
                    @event = item.Name,
                    distinct_id = item.InstallationId,
                    properties = CaptureProperties(item),
                    timestamp = item.Timestamp,
                    uuid = item.Id,
                }
            );
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, item.Consent);
            timeout.CancelAfter(SendTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (!CanDeliver(item))
            {
                return;
            }

            timeout.Token.ThrowIfCancellationRequested();
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            // Rejections (including blocked networks, rate limits, and server errors) are dropped without retries.
        }
        catch
        {
            // Never include HTTP errors, payloads, or exception strings in application logs.
        }
    }

    private bool CanDeliver(QueuedEvent item) =>
        _enabled
        && Volatile.Read(ref _stopRequested) == 0
        && item.Generation == Interlocked.Read(ref _generation)
        && !item.Consent.IsCancellationRequested;

    private Dictionary<string, object> CaptureProperties(QueuedEvent item)
    {
        var properties = new Dictionary<string, object>
        {
            ["distinct_id"] = item.InstallationId,
            ["$session_id"] = item.SessionId,
            ["app_version"] = _appVersion,
            ["windows_version"] = _windowsVersion,
            ["platform"] = "windows",
            ["environment"] = _environment!,
            ["$process_person_profile"] = false,
            ["$geoip_disable"] = true,
        };
        if (item.Name == "app_opened")
        {
            try
            {
                // Local reads stay on the worker, outside application locks; failed enrichment must not lose the base event.
                _windowsContext().AddTo(properties);
            }
            catch { }
        }
        var values = item.Properties;
        if (values is null)
        {
            return properties;
        }

        Add("outcome", OutcomeName(values.Outcome));
        Add("page", PageName(values.Page));
        Add("error_kind", ErrorName(values.ErrorKind));
        Add("duration_ms", values.DurationMs is >= 0 and <= 86_400_000 ? Math.Round(values.DurationMs.Value) : null);
        Add("target_count", NonNegative(values.TargetCount));
        Add("copied_count", NonNegative(values.CopiedCount));
        Add("verified_count", NonNegative(values.VerifiedCount));
        Add("failed_count", NonNegative(values.FailedCount));
        Add("api_requested", values.ApiRequested);
        Add("api_completed", values.ApiCompleted);
        Add("operation_id", values.OperationId is { } operationId && operationId != Guid.Empty ? operationId.ToString("D") : null);
        Add("workflow", WorkflowName(values.Workflow));
        Add("requested_width", values.OutputWidth is > 0 and <= 32768 ? values.OutputWidth : null);
        Add("requested_fps", values.TargetFps is >= 0 and <= 1000 ? values.TargetFps : null);
        Add("clip_duration_seconds", values.ClipDurationSeconds is >= 0 and <= 86400 ? values.ClipDurationSeconds : null);
        Add("selected_frame_count", values.SelectedFrameCount is > 0 and <= 10_000_000 ? values.SelectedFrameCount : null);
        Add("uses_reference_gif", values.UsesReferenceGif);
        Add("gif_size_bytes", values.GifSizeBytes is > 0 and <= 9_007_199_254_740_991L ? values.GifSizeBytes : null);
        Add("gif_width", values.GifWidth is > 0 and <= ushort.MaxValue ? values.GifWidth : null);
        Add("gif_height", values.GifHeight is > 0 and <= ushort.MaxValue ? values.GifHeight : null);
        return properties;

        void Add(string name, object? value)
        {
            if (value is not null)
            {
                properties.Add(name, value);
            }
        }
    }

    private void LoadPreferences()
    {
        try
        {
            using var stream = File.OpenRead(_preferencesPath);
            _hasSavedPreference = true;
            if (stream.Length > 4096)
            {
                return;
            }

            var settings = JsonSerializer.Deserialize<Preferences>(stream);
            if (settings is not { Version: 1, Enabled: not null })
            {
                return;
            }

            if (settings.Enabled.Value && !Guid.TryParseExact(settings.InstallationId, "D", out _))
            {
                return;
            }

            _enabled = settings.Enabled.Value;
            _installationId = _enabled ? settings.InstallationId : null;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch
        {
            // An unreadable preference is not a first launch: never let startup defaults override it.
            _hasSavedPreference = true;
        }
    }

    private void EnsureSession(DateTimeOffset timestamp)
    {
        if (
            _sessionId is null
            || timestamp - _lastActivity >= TimeSpan.FromMinutes(30)
            || timestamp - _sessionStarted >= TimeSpan.FromHours(24)
            || timestamp < _lastActivity
        )
        {
            _sessionStarted = timestamp;
            _sessionId = Guid.CreateVersion7(timestamp).ToString("D");
        }

        _lastActivity = timestamp;
    }

    // Caller holds only the short-lived state lock; cancellation always runs after releasing it.
    private CancellationTokenSource DisableCollection()
    {
        _enabled = false;
        _installationId = null;
        _sessionId = null;
        Interlocked.Increment(ref _generation);
        _pending.Clear();
        var previous = _consent;
        _consent = new CancellationTokenSource();
        return previous;
    }

    private bool SavePreferences(Preferences settings)
    {
        string? temporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_preferencesPath))!;
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, "analytics-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings));
            File.Move(temporaryPath, _preferencesPath, overwrite: true);
            return true;
        }
        catch
        {
            // Preserve the previous choice on disk. The caller reports that the new choice was not saved.
            return false;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch { }
            }
        }
    }

    private void SignalWorker()
    {
        try
        {
            if (_available.CurrentCount == 0)
            {
                _available.Release();
            }
        }
        catch (SemaphoreFullException) { }
    }

    private static void RequestCancellation(CancellationTokenSource? source, bool disposeWhenDone = false)
    {
        if (source is null)
        {
            return;
        }

        try
        {
            // CancelAsync marks cancellation immediately but runs arbitrary transport callbacks asynchronously.
            _ = ObserveCancellationAsync(source.CancelAsync(), source, disposeWhenDone);
        }
        catch { }
    }

    private static async Task ObserveCancellationAsync(Task cancellation, CancellationTokenSource source, bool disposeWhenDone)
    {
        try
        {
            await cancellation.ConfigureAwait(false);
        }
        catch { }
        finally
        {
            if (disposeWhenDone)
            {
                source.Dispose();
            }
        }
    }

    private static Uri? GetEndpoint(string host)
    {
        if (
            !Uri.TryCreate(host, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0
            || uri.AbsolutePath != "/"
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0
            || uri.Host is not ("eu.i.posthog.com" or "us.i.posthog.com")
        )
        {
            return null;
        }

        return new Uri(uri, "capture/");
    }

    private static string SafeVersion(string version) =>
        !string.IsNullOrWhiteSpace(version)
        && version.Length <= 64
        && version.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '+')
            ? version
            : "unknown";

    private static int? NonNegative(int? value) => value is >= 0 ? value : null;

    private static string? EventName(AnalyticsEvent value) =>
        value switch
        {
            AnalyticsEvent.AppOpened => "app_opened",
            AnalyticsEvent.PageViewed => "page_viewed",
            AnalyticsEvent.GifSelected => "gif_selected",
            AnalyticsEvent.VideoLoadStarted => "video_load_started",
            AnalyticsEvent.VideoLoadCompleted => "video_load_completed",
            AnalyticsEvent.GifGenerationStarted => "gif_generation_started",
            AnalyticsEvent.GifGenerationCompleted => "gif_generation_completed",
            AnalyticsEvent.GifSaveCompleted => "gif_save_completed",
            AnalyticsEvent.LockscreenApplyStarted => "lockscreen_apply_started",
            AnalyticsEvent.LockscreenApplyCompleted => "lockscreen_apply_completed",
            AnalyticsEvent.LockscreenRemovalCompleted => "lockscreen_removal_completed",
            AnalyticsEvent.DiagnosticTestRequested => "diagnostic_test_requested",
            AnalyticsEvent.DiagnosticStopRequested => "diagnostic_stop_requested",
            AnalyticsEvent.DiagnosticReportExportCompleted => "diagnostic_report_export_completed",
            AnalyticsEvent.AppError => "app_error",
            AnalyticsEvent.LogExportCompleted => "log_export_completed",
            _ => null,
        };

    private static string? OutcomeName(AnalyticsOutcome? value) =>
        value switch
        {
            AnalyticsOutcome.Succeeded => "succeeded",
            AnalyticsOutcome.Failed => "failed",
            AnalyticsOutcome.Cancelled => "cancelled",
            AnalyticsOutcome.Partial => "partial",
            AnalyticsOutcome.NoChange => "no_change",
            _ => null,
        };

    private static string? PageName(AnalyticsPage? value) =>
        value switch
        {
            AnalyticsPage.Lockscreen => "lockscreen",
            AnalyticsPage.Diagnostics => "diagnostics",
            AnalyticsPage.Settings => "settings",
            _ => null,
        };

    private static string? WorkflowName(AnalyticsWorkflow? value) =>
        value switch
        {
            AnalyticsWorkflow.Lockscreen => "lockscreen",
            AnalyticsWorkflow.Diagnostics => "diagnostics",
            _ => null,
        };

    private static string? ErrorName(AnalyticsErrorKind? value) =>
        value switch
        {
            AnalyticsErrorKind.Cancelled => "cancelled",
            AnalyticsErrorKind.PermissionDenied => "permission_denied",
            AnalyticsErrorKind.InvalidMedia => "invalid_media",
            AnalyticsErrorKind.Io => "io",
            AnalyticsErrorKind.Timeout => "timeout",
            AnalyticsErrorKind.Other => "other",
            _ => null,
        };

    private sealed record Preferences(int Version, bool? Enabled, string? InstallationId);

    private sealed record QueuedEvent(
        string Name,
        AnalyticsProperties? Properties,
        string InstallationId,
        string SessionId,
        DateTimeOffset Timestamp,
        Guid Id,
        long Generation,
        CancellationToken Consent
    );
}
