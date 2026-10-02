using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using LockscreenGif.Contracts.Services;
using LockscreenGif.Models.Diagnostics;
using LockscreenGif.Services;
using LockscreenGif.Services.Analytics;
using LockscreenGif.Services.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace LockscreenGif.ViewModels;

public sealed class DiagnosticsViewModel : ObservableObject
{
    private readonly DiagnosticsSessionService _service;
    private readonly ILockscreenService _lockscreen;
    private readonly AnalyticsService _analytics;
    private DispatcherQueue? _dispatcher;
    private DispatcherQueueTimer? _timer;
    private DiagnosticSession? _session;
    private bool _busy;
    private bool _stopping;
    private bool _useReference;
    private bool _attached;
    private string _notice = string.Empty;
    private int _refreshQueued;
    private string? _findingsSession;

    public DiagnosticsViewModel(
        DiagnosticsSessionService service,
        ILockscreenService lockscreen,
        AnalyticsService analytics,
        LockscreenPreferences preferences
    )
    {
        _service = service;
        _lockscreen = lockscreen;
        _analytics = analytics;
        Preferences = preferences;
    }

    public ObservableCollection<DiagnosticSetupWarning> SetupWarnings { get; } = [];
    public Visibility SetupWarningsVisibility =>
        _session is null && !IsRunning && SetupWarnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public ObservableCollection<DiagnosticFindingViewModel> Findings { get; } = [];
    public bool UseReference
    {
        get => _useReference;
        set
        {
            if (SetProperty(ref _useReference, value))
            {
                OnPropertyChanged(nameof(SourceDescription));
                OnPropertyChanged(nameof(CanStart));
                Refresh();
            }
        }
    }
    public bool UseWindowsApi
    {
        get => Preferences.UseWindowsApi;
        set => Preferences.UseWindowsApi = value;
    }
    public LockscreenPreferences Preferences { get; }
    public string Notice
    {
        get => _notice;
        private set
        {
            SetProperty(ref _notice, value);
            OnPropertyChanged(nameof(HasNotice));
        }
    }
    public bool HasNotice => !string.IsNullOrWhiteSpace(Notice);
    public bool IsRunning => _service.IsRunning;
    public bool CanConfigure => !_busy && !_stopping && !IsRunning;
    public bool CanStart => CanConfigure && !_service.IsVerificationRunning && (UseReference || _lockscreen.CurrentImage is not null);
    public bool CanStop => IsRunning && !_stopping;
    public bool CanLock => !_busy && !_stopping && IsRunning && _session?.Phase == "Waiting for lock";
    public bool CanExport => CanConfigure && _session is not null;
    public Visibility SessionVisibility => _session is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ResultVisibility => _session is not null && !IsRunning ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RunningVisibility => IsRunning ? Visibility.Visible : Visibility.Collapsed;
    public string Phase => _session?.Phase ?? "Ready when you are";
    public string SessionDescription =>
        _session is null ? string.Empty : $"{_session.SourceName} · Windows API {(_session.UseWindowsApi ? "on" : "off")} · {Elapsed}";
    public string SourceDescription =>
        IsRunning && _session is not null
            ? string.IsNullOrWhiteSpace(_session.SourceName)
                ? "Preparing the test source…"
                : _session.SourceName
            : UseReference
                ? "A small reference GIF with obvious movement will be used."
                : _lockscreen.CurrentImage is { } source
                    ? source.Name
                    : "Choose or convert a GIF on the Lockscreen page first.";
    public string ProgressDescription =>
        _session?.Phase switch
        {
            "Preparing" => "Recording the baseline and checking the source animation.",
            "Applying" => "Applying the GIF and checking each destination. Accept the Windows permission request if prompted.",
            "Waiting for lock" => "Ready to lock. Check whether the GIF animates, then unlock when you're ready.",
            "Locked" => "Monitoring the cache while the computer is locked.",
            "Collecting final evidence" => "Collecting the final file inventory after unlocking.",
            _ => _session?.Error ?? "Review the findings and export a report.",
        };
    private string Elapsed =>
        FormatElapsed(_session is null ? TimeSpan.Zero : (_session.EndedAt ?? DateTimeOffset.UtcNow) - _session.StartedAt);

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return $"{Math.Floor(elapsed.TotalHours):00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    public void Attach(DispatcherQueue dispatcher)
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        _dispatcher = dispatcher;
        _service.Changed += OnServiceChanged;
        Preferences.PropertyChanged += OnPreferencesChanged;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += OnTimerTick;
        _timer.Start();
        Refresh();
    }

    public void Detach()
    {
        _attached = false;
        _service.Changed -= OnServiceChanged;
        Preferences.PropertyChanged -= OnPreferencesChanged;
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTimerTick;
            _timer = null;
        }
        _dispatcher = null;
    }

    private void OnTimerTick(DispatcherQueueTimer sender, object args) => OnPropertyChanged(nameof(SessionDescription));

    private void OnPreferencesChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(LockscreenPreferences.UseWindowsApi) or nameof(LockscreenPreferences.HasSaveError))
        {
            OnServiceChanged(sender, EventArgs.Empty);
        }
    }

    private void OnServiceChanged(object? sender, EventArgs args)
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0)
        {
            return;
        }

        if (
            _dispatcher?.TryEnqueue(() =>
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
                if (_attached)
                {
                    Refresh();
                }
            }) != true
        )
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
        }
    }

    public Task RefreshReadinessAsync() => RunAsync(_service.RefreshReadinessAsync);

    public Task StartAsync() =>
        RunAsync(async () =>
        {
            _analytics.Track(
                AnalyticsEvent.DiagnosticTestRequested,
                new()
                {
                    Workflow = AnalyticsWorkflow.Diagnostics,
                    UsesReferenceGif = UseReference,
                    ApiRequested = Preferences.UseWindowsApi,
                }
            );
            await _service.StartAsync(UseReference, Preferences.UseWindowsApi);
        });

    public async Task StopAsync()
    {
        if (_stopping || !IsRunning)
        {
            return;
        }

        _stopping = true;
        _analytics.Track(AnalyticsEvent.DiagnosticStopRequested, new() { Workflow = AnalyticsWorkflow.Diagnostics });
        Refresh();
        try
        {
            await _service.StopAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            _stopping = false;
            Refresh();
        }
    }

    public async Task ExportAsync(string path) =>
        await RunAsync(async () =>
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                var exportedPath = await _service.ExportAsync(path);
                Notice = $"Report saved to {exportedPath}";
                _analytics.Track(
                    AnalyticsEvent.DiagnosticReportExportCompleted,
                    new()
                    {
                        Workflow = AnalyticsWorkflow.Diagnostics,
                        Outcome = AnalyticsOutcome.Succeeded,
                        DurationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    }
                );
            }
            catch (Exception ex)
            {
                _analytics.TrackFailure(
                    AnalyticsEvent.DiagnosticReportExportCompleted,
                    ex,
                    new AnalyticsProperties
                    {
                        Workflow = AnalyticsWorkflow.Diagnostics,
                        DurationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    }
                );
                throw;
            }
        });

    public void LockNow()
    {
        try
        {
            if (!_service.TryLock(out var error))
            {
                Notice = error ?? "Windows could not lock this session. Try Win+L.";
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    public void ShowError(Exception error)
    {
        _analytics.CaptureException(
            error,
            AnalyticsErrorContext.DiagnosticsAction,
            new AnalyticsProperties { Workflow = AnalyticsWorkflow.Diagnostics }
        );
        Notice = $"{error.GetType().Name}: {error.Message}";
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        Notice = string.Empty;
        Refresh();
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    private void Refresh()
    {
        _session = _service.CurrentForDisplay;
        SetupWarnings.Clear();
        foreach (var check in _service.Readiness)
        {
            if (DiagnosticSetupWarning.FromCheck(check) is { } warning)
            {
                SetupWarnings.Add(warning);
            }
        }

        var expanded =
            _findingsSession == _session?.Id
                ? Findings.Where(item => item.IsExpanded).Select(item => item.Title).ToHashSet()
                : new HashSet<string>();
        _findingsSession = _session?.Id;
        Findings.Clear();
        foreach (var finding in _session?.Findings ?? [])
        {
            Findings.Add(new DiagnosticFindingViewModel(finding) { IsExpanded = expanded.Contains(finding.Title) });
        }

        foreach (
            var property in new[]
            {
                nameof(IsRunning),
                nameof(UseWindowsApi),
                nameof(CanConfigure),
                nameof(CanStart),
                nameof(CanStop),
                nameof(CanLock),
                nameof(CanExport),
                nameof(SessionVisibility),
                nameof(ResultVisibility),
                nameof(RunningVisibility),
                nameof(SetupWarningsVisibility),
                nameof(Phase),
                nameof(SessionDescription),
                nameof(SourceDescription),
                nameof(ProgressDescription),
            }
        )
        {
            OnPropertyChanged(property);
        }
    }
}
