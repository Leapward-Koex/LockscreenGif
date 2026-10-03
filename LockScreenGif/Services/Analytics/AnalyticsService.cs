using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LockscreenGif.Contracts.Services;
using LockscreenGif.Models;

namespace LockscreenGif.Services.Analytics;

/// <summary>Best-effort analytics gated by the saved sharing preference. Events only live in a small in-memory queue.</summary>
public sealed class AnalyticsService : IDisposable, IErrorReporter
{
    private const int QueueCapacity = 64;
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromMilliseconds(1500);
    private readonly object _sync = new();
    private readonly object _preferencesSync = new();
    private readonly Queue<QueuedEvent> _pending = new();
    private readonly ConditionalWeakTable<Exception, CapturedMarker> _capturedExceptions = new();
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
                if (!enabled && _enabled)
                {
                    var optOut = CaptureOptOut();
                    cancelledConsent = DisableCollection();
                    if (optOut is not null)
                    {
                        // Only this final snapshot survives opt-out. Ordinary queued/in-flight events are revoked.
                        _pending.Enqueue(optOut with { Generation = Interlocked.Read(ref _generation), Consent = _consent.Token });
                        SignalWorker();
                    }
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

    public void Track(AnalyticsEvent eventName, AnalyticsProperties? properties = null) => TrackCore(eventName, properties);

    public void TrackFailure(AnalyticsEvent eventName, Exception exception, AnalyticsProperties? properties = null, bool handled = true)
    {
        try
        {
            var failure = (properties ?? new AnalyticsProperties()).WithFailure(exception);
            Track(eventName, failure);
            if (ErrorContextFor(eventName) is { } context)
            {
                CaptureException(exception, context, failure, handled);
            }
        }
        catch
        {
            // Optional error reporting must not replace the original operation error.
        }
    }

    public void CaptureException(
        Exception exception,
        AnalyticsErrorContext context,
        AnalyticsProperties? properties = null,
        bool handled = true
    )
    {
        try
        {
            if (_stopping || !_enabled || !_allowSending || !IsConfigured || ErrorContextName(context) is null)
            {
                return;
            }
            var failure = (properties ?? new AnalyticsProperties()).WithFailure(exception);
            if (handled && failure.ErrorKind == AnalyticsErrorKind.Cancelled)
            {
                return;
            }
            // A rethrow can reach a second UI/fatal boundary. Weak keys neither retain exceptions nor grow a permanent history.
            var marker = _capturedExceptions.GetValue(AnalyticsErrorDetails.Unwrap(exception), static _ => new CapturedMarker());
            if (Interlocked.Exchange(ref marker.Captured, 1) != 0)
            {
                return;
            }
            if (properties?.Outcome == AnalyticsOutcome.Partial)
            {
                failure = failure with { Outcome = AnalyticsOutcome.Partial };
            }
            // Only typed snapshots enter the queue; the exception, its Data and raw stack/message never do.
            TrackCore(AnalyticsEvent.Exception, failure, new CapturedException(context, handled));
        }
        catch
        {
            // Never recurse into error tracking if constructing or queueing an exception fails.
        }
    }

    void IErrorReporter.CaptureException(Exception exception, AnalyticsErrorContext context, AnalyticsWorkflow workflow) =>
        CaptureException(exception, context, new AnalyticsProperties { Workflow = workflow });

    private void TrackCore(AnalyticsEvent eventName, AnalyticsProperties? properties, CapturedException? capturedException = null)
    {
        var lockTaken = false;
        try
        {
            if (
                _stopping
                || !_enabled
                || !_allowSending
                || !IsConfigured
                || eventName == AnalyticsEvent.AnalyticsOptedOut
                || (eventName == AnalyticsEvent.Exception && capturedException is null)
            )
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
                    _consent.Token,
                    Exception: capturedException
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
            var remaining = item.IsOptOut ? SendTimeout - (_timeProvider.GetUtcNow() - item.Timestamp) : SendTimeout;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }
            timeout.CancelAfter(remaining);
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
        (_enabled || item.IsOptOut)
        && Volatile.Read(ref _stopRequested) == 0
        && item.Generation == Interlocked.Read(ref _generation)
        && !item.Consent.IsCancellationRequested
        && (!item.IsOptOut || _timeProvider.GetUtcNow() - item.Timestamp < SendTimeout);

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

        if (item.Exception is { } error)
        {
            AddExceptionProperties(properties, values, error);
        }

        Add("outcome", OutcomeName(values.Outcome));
        Add("page", PageName(values.Page));
        Add("error_kind", ErrorName(values.ErrorKind));
        Add("exception_type", ExceptionTypeName(values.ExceptionType));
        Add("error_hresult", values.ErrorHResult);
        Add(
            "error_component",
            values.ErrorComponent switch
            {
                MediaProcessingComponent.Ffmpeg => "ffmpeg",
                MediaProcessingComponent.Gifski => "gifski",
                _ => null,
            }
        );
        Add("native_error_code", values.NativeErrorCode);
        Add("duration_ms", values.DurationMs is >= 0 and <= 86_400_000 ? Math.Round(values.DurationMs.Value) : null);
        Add("target_count", NonNegative(values.TargetCount));
        Add("copied_count", NonNegative(values.CopiedCount));
        Add("verified_count", NonNegative(values.VerifiedCount));
        Add("failed_count", NonNegative(values.FailedCount));
        Add("api_requested", values.ApiRequested);
        Add("api_completed", values.ApiCompleted);
        Add(
            "apply_failure_reason",
            values.ApplyFailureReason switch
            {
                LockscreenApplyFailureReason.SourceReadFailed => "source_read_failed",
                LockscreenApplyFailureReason.InvalidSource => "invalid_source",
                LockscreenApplyFailureReason.WindowsApiFailed => "windows_api_failed",
                LockscreenApplyFailureReason.CacheInaccessible => "cache_inaccessible",
                LockscreenApplyFailureReason.CacheMissing => "cache_missing",
                LockscreenApplyFailureReason.NoDestinations => "no_destinations",
                LockscreenApplyFailureReason.CacheDiscoveryFailed => "cache_discovery_failed",
                LockscreenApplyFailureReason.CopyFailed => "copy_failed",
                LockscreenApplyFailureReason.VerificationFailed => "verification_failed",
                LockscreenApplyFailureReason.Unknown => "unknown",
                _ => null,
            }
        );
        Add("operation_id", values.OperationId is { } operationId && operationId != Guid.Empty ? operationId.ToString("D") : null);
        Add("workflow", WorkflowName(values.Workflow));
        Add(
            "lockscreen_source",
            values.LockscreenSource switch
            {
                LockscreenSourceKind.Video => "video",
                LockscreenSourceKind.UserGif => "user_gif",
                LockscreenSourceKind.BundledGif => "bundled_gif",
                LockscreenSourceKind.Unknown => "unknown",
                _ => null,
            }
        );
        Add("requested_width", values.OutputWidth is > 0 and <= 32768 ? values.OutputWidth : null);
        Add("requested_fps", values.TargetFps is >= 0 and <= 1000 ? values.TargetFps : null);
        Add(
            "requested_fps_mode",
            values.TargetFps is >= 0 and <= 1000
                ? values.TargetFps == 0
                    ? "all_source_frames"
                    : "target"
                : null
        );
        Add("source_fps", values.SourceFps is > 0 and <= 1000 ? values.SourceFps : null);
        Add("clip_duration_seconds", values.ClipDurationSeconds is >= 0 and <= 86400 ? values.ClipDurationSeconds : null);
        Add("selected_frame_count", values.SelectedFrameCount is > 0 and <= 10_000_000 ? values.SelectedFrameCount : null);
        Add("extracted_frame_count", values.ExtractedFrameCount is > 0 and <= 10_000_000 ? values.ExtractedFrameCount : null);
        Add("failure_stage", GenerationStageName(values.FailureStage));
        Add("media_load_stage", MediaLoadStageName(values.MediaLoadStage));
        Add("metadata_fallback_used", values.MetadataFallbackUsed);
        Add("playback_available", values.PlaybackAvailable);
        Add("failure_stage_duration_ms", Duration(values.FailureStageDurationMs));
        Add("extraction_duration_ms", Duration(values.ExtractionDurationMs));
        Add("encoding_duration_ms", Duration(values.EncodingDurationMs));
        Add("preview_duration_ms", Duration(values.PreviewDurationMs));
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

    private static void AddExceptionProperties(Dictionary<string, object> properties, AnalyticsProperties values, CapturedException error)
    {
        var context = ErrorContextName(error.Context)!;
        var type = PostHogExceptionTypeName(values);
        var kind = ErrorName(values.ErrorKind) ?? "other";
        var hresult = values.ErrorHResult?.ToString(CultureInfo.InvariantCulture) ?? "none";
        var nativeCode = values.NativeErrorCode?.ToString(CultureInfo.InvariantCulture) ?? "none";
        var component = values.ErrorComponent switch
        {
            MediaProcessingComponent.Ffmpeg => "ffmpeg",
            MediaProcessingComponent.Gifski => "gifski",
            _ => "none",
        };
        var group = string.Join(
            '|',
            "v1",
            context,
            type,
            kind,
            hresult,
            component,
            nativeCode,
            GenerationStageName(values.FailureStage) ?? "none"
        );
        if (MediaLoadStageName(values.MediaLoadStage) is { } loadStage)
        {
            group += "|media_load:" + loadStage;
        }
        properties["error_context"] = context;
        properties["$exception_list"] = new[]
        {
            new
            {
                type,
                // This is a fixed-category description, never Exception.Message or a stack frame.
                value = $"{context}: {kind} (HRESULT {hresult}; native code {nativeCode})",
                mechanism = new
                {
                    handled = error.Handled,
                    synthetic = false,
                    type = "manual",
                },
            },
        };
        properties["$exception_fingerprint"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(group))).ToLowerInvariant();
    }

    private static string PostHogExceptionTypeName(AnalyticsProperties values) =>
        values.ExceptionType switch
        {
            AnalyticsExceptionType.Cancelled => "OperationCanceledException",
            AnalyticsExceptionType.UnauthorizedAccess => "UnauthorizedAccessException",
            AnalyticsExceptionType.Security => "SecurityException",
            AnalyticsExceptionType.InvalidData => "InvalidDataException",
            AnalyticsExceptionType.Format => "FormatException",
            AnalyticsExceptionType.Timeout => "TimeoutException",
            AnalyticsExceptionType.FileNotFound => "FileNotFoundException",
            AnalyticsExceptionType.DirectoryNotFound => "DirectoryNotFoundException",
            AnalyticsExceptionType.Io => "IOException",
            AnalyticsExceptionType.OutOfMemory => "OutOfMemoryException",
            AnalyticsExceptionType.DllNotFound => "DllNotFoundException",
            AnalyticsExceptionType.EntryPointNotFound => "EntryPointNotFoundException",
            AnalyticsExceptionType.BadImageFormat => "BadImageFormatException",
            AnalyticsExceptionType.Win32 => "Win32Exception",
            AnalyticsExceptionType.Com => "COMException",
            AnalyticsExceptionType.InvalidOperation => "InvalidOperationException",
            AnalyticsExceptionType.Argument => "ArgumentException",
            AnalyticsExceptionType.Aggregate => "AggregateException",
            AnalyticsExceptionType.MediaProcessing => values.ErrorComponent switch
            {
                MediaProcessingComponent.Ffmpeg => "FFmpegError",
                MediaProcessingComponent.Gifski => "GifskiError",
                _ => "MediaProcessingException",
            },
            _ => "Exception",
        };

    private static AnalyticsErrorContext? ErrorContextFor(AnalyticsEvent eventName) =>
        eventName switch
        {
            AnalyticsEvent.GifSelected => AnalyticsErrorContext.GifSelection,
            AnalyticsEvent.VideoLoadCompleted => AnalyticsErrorContext.VideoLoad,
            AnalyticsEvent.GifGenerationCompleted => AnalyticsErrorContext.GifGeneration,
            AnalyticsEvent.GifSaveCompleted => AnalyticsErrorContext.GifSave,
            AnalyticsEvent.LockscreenApplyCompleted => AnalyticsErrorContext.LockscreenApply,
            AnalyticsEvent.LockscreenRemovalCompleted => AnalyticsErrorContext.LockscreenRemoval,
            AnalyticsEvent.DiagnosticReportExportCompleted => AnalyticsErrorContext.DiagnosticReportExport,
            AnalyticsEvent.LogExportCompleted => AnalyticsErrorContext.LogExport,
            AnalyticsEvent.AppError => AnalyticsErrorContext.AppCrash,
            _ => null,
        };

    private static string? ErrorContextName(AnalyticsErrorContext context) =>
        context switch
        {
            AnalyticsErrorContext.GifSelection => "gif_selection",
            AnalyticsErrorContext.VideoLoad => "video_load",
            AnalyticsErrorContext.GifGeneration => "gif_generation",
            AnalyticsErrorContext.GifSave => "gif_save",
            AnalyticsErrorContext.LockscreenApply => "lockscreen_apply",
            AnalyticsErrorContext.LockscreenRemoval => "lockscreen_removal",
            AnalyticsErrorContext.DiagnosticReportExport => "diagnostic_report_export",
            AnalyticsErrorContext.LogExport => "log_export",
            AnalyticsErrorContext.AppCrash => "app_crash",
            AnalyticsErrorContext.MainAction => "main_action",
            AnalyticsErrorContext.DiagnosticsAction => "diagnostics_action",
            AnalyticsErrorContext.LogFolderOpen => "log_folder_open",
            AnalyticsErrorContext.AppShutdown => "app_shutdown",
            AnalyticsErrorContext.VideoPreview => "video_preview",
            AnalyticsErrorContext.VideoThumbnails => "video_thumbnails",
            AnalyticsErrorContext.DiagnosticRun => "diagnostic_run",
            AnalyticsErrorContext.DiagnosticTrace => "diagnostic_trace",
            AnalyticsErrorContext.LockscreenVerification => "lockscreen_verification",
            _ => null,
        };

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

    // Called only for an enabled -> disabled transition, before identifiers are removed. Never performs network work.
    private QueuedEvent? CaptureOptOut()
    {
        try
        {
            if (_stopping || !_allowSending || !IsConfigured)
            {
                return null;
            }

            var timestamp = _timeProvider.GetUtcNow();
            EnsureSession(timestamp);
            return new QueuedEvent(
                EventName(AnalyticsEvent.AnalyticsOptedOut)!,
                null,
                _installationId!,
                _sessionId!,
                timestamp,
                Guid.CreateVersion7(timestamp),
                0,
                default,
                IsOptOut: true
            );
        }
        catch
        {
            return null;
        }
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
            AnalyticsEvent.Exception => "$exception",
            AnalyticsEvent.GifSaveCompleted => "gif_save_completed",
            AnalyticsEvent.LockscreenApplyStarted => "lockscreen_apply_started",
            AnalyticsEvent.LockscreenApplyCompleted => "lockscreen_apply_completed",
            AnalyticsEvent.LockscreenRemovalCompleted => "lockscreen_removal_completed",
            AnalyticsEvent.DiagnosticTestRequested => "diagnostic_test_requested",
            AnalyticsEvent.DiagnosticStopRequested => "diagnostic_stop_requested",
            AnalyticsEvent.DiagnosticReportExportCompleted => "diagnostic_report_export_completed",
            AnalyticsEvent.AppError => "app_error",
            AnalyticsEvent.LogExportCompleted => "log_export_completed",
            AnalyticsEvent.AnalyticsOptedOut => "analytics_opted_out",
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
            AnalyticsErrorKind.OutOfMemory => "out_of_memory",
            AnalyticsErrorKind.DiskFull => "disk_full",
            AnalyticsErrorKind.DependencyMissing => "dependency_missing",
            AnalyticsErrorKind.DependencyIncompatible => "dependency_incompatible",
            AnalyticsErrorKind.NativeFailure => "native_failure",
            AnalyticsErrorKind.InvalidState => "invalid_state",
            AnalyticsErrorKind.InvalidArgument => "invalid_argument",
            AnalyticsErrorKind.CodecMissing => "codec_missing",
            AnalyticsErrorKind.SecurityPolicyBlocked => "security_policy_blocked",
            _ => null,
        };

    private static double? Duration(double? value) => value is >= 0 and <= 86_400_000 ? Math.Round(value.Value) : null;

    private static string? GenerationStageName(AnalyticsGenerationStage? value) =>
        value switch
        {
            AnalyticsGenerationStage.Preparing => "preparing",
            AnalyticsGenerationStage.ExtractingFrames => "extracting_frames",
            AnalyticsGenerationStage.EncodingGif => "encoding_gif",
            AnalyticsGenerationStage.OpeningOutput => "opening_output",
            AnalyticsGenerationStage.LoadingPreview => "loading_preview",
            AnalyticsGenerationStage.Completing => "completing",
            _ => null,
        };

    private static string? MediaLoadStageName(AnalyticsMediaLoadStage? value) =>
        value switch
        {
            AnalyticsMediaLoadStage.PickingFile => "picking_file",
            AnalyticsMediaLoadStage.ReadingMetadata => "reading_metadata",
            AnalyticsMediaLoadStage.IndexingFrames => "indexing_frames",
            AnalyticsMediaLoadStage.OpeningPreview => "opening_preview",
            AnalyticsMediaLoadStage.OpeningFile => "opening_file",
            AnalyticsMediaLoadStage.DecodingImage => "decoding_image",
            AnalyticsMediaLoadStage.Completing => "completing",
            AnalyticsMediaLoadStage.PlayingPreview => "playing_preview",
            AnalyticsMediaLoadStage.ReadingFallbackMetadata => "reading_fallback_metadata",
            _ => null,
        };

    private static string? ExceptionTypeName(AnalyticsExceptionType? value) =>
        value switch
        {
            AnalyticsExceptionType.Cancelled => "cancelled",
            AnalyticsExceptionType.UnauthorizedAccess => "unauthorized_access",
            AnalyticsExceptionType.Security => "security",
            AnalyticsExceptionType.InvalidData => "invalid_data",
            AnalyticsExceptionType.Format => "format",
            AnalyticsExceptionType.Timeout => "timeout",
            AnalyticsExceptionType.FileNotFound => "file_not_found",
            AnalyticsExceptionType.DirectoryNotFound => "directory_not_found",
            AnalyticsExceptionType.Io => "io",
            AnalyticsExceptionType.OutOfMemory => "out_of_memory",
            AnalyticsExceptionType.DllNotFound => "dll_not_found",
            AnalyticsExceptionType.EntryPointNotFound => "entry_point_not_found",
            AnalyticsExceptionType.BadImageFormat => "bad_image_format",
            AnalyticsExceptionType.Win32 => "win32",
            AnalyticsExceptionType.Com => "com",
            AnalyticsExceptionType.InvalidOperation => "invalid_operation",
            AnalyticsExceptionType.Argument => "argument",
            AnalyticsExceptionType.Aggregate => "aggregate",
            AnalyticsExceptionType.MediaProcessing => "media_processing",
            AnalyticsExceptionType.Other => "other",
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
        CancellationToken Consent,
        bool IsOptOut = false,
        CapturedException? Exception = null
    );

    private sealed record CapturedException(AnalyticsErrorContext Context, bool Handled);

    private sealed class CapturedMarker
    {
        public int Captured;
    }
}
