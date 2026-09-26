using LockscreenGif.Contracts.Services;
using LockscreenGif.Models.Diagnostics;

namespace LockscreenGif.Services.Diagnostics;

/// <summary>The current test stays in memory independently of page navigation.</summary>
public sealed class DiagnosticsSessionService
{
    private readonly ILockscreenService _lockscreen;
    private readonly WindowsSessionMonitor _windows;
    private readonly PrivilegedSessionFactory _privileged;
    private DiagnosticRun? _run;
    private IReadOnlyList<DiagnosticCheck> _readiness = Array.Empty<DiagnosticCheck>();
    private int _starting;
    public event EventHandler? Changed;

    public DiagnosticsSessionService(ILockscreenService lockscreen, WindowsSessionMonitor windows, PrivilegedSessionFactory privileged)
    {
        _lockscreen = lockscreen;
        _windows = windows;
        _privileged = privileged;
    }

    public DiagnosticSession? Current => _run?.Recorder.Snapshot();
    public DiagnosticSession? CurrentForDisplay => _run?.Recorder.Snapshot(includeTraceDetails: false);
    public bool IsRunning => Volatile.Read(ref _starting) != 0 || _run is { IsFinished: false };
    public IReadOnlyList<DiagnosticCheck> Readiness => _readiness;

    public async Task StartAsync(bool useReference, bool useWindowsApi)
    {
        if (Interlocked.CompareExchange(ref _starting, 1, 0) != 0)
        {
            throw new InvalidOperationException("A test is already starting.");
        }

        try
        {
            if (_run is { IsFinished: false } || _lockscreen.IsApplying)
            {
                throw new InvalidOperationException("Wait for the current operation to finish.");
            }

            var source = _lockscreen.CurrentImage?.Path ?? "";
            var session = new DiagnosticSession { UseReference = useReference, UseWindowsApi = useWindowsApi };
            if (_run is not null)
            {
                _run.Recorder.Changed -= Notify;
                _run.Finished -= OnFinished;
            }
            var run = new DiagnosticRun(_lockscreen, _windows, session, source, _privileged.Create(_lockscreen.CacheDirectory));
            _run = run;
            run.Recorder.Changed += Notify;
            run.Finished += OnFinished;
            Notify();
            await run.StartAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _starting, 0);
            Notify();
        }
    }

    private void OnFinished(DiagnosticRun completedRun) => Notify();

    public async Task StopAsync()
    {
        if (_run is not null && !_run.IsFinished)
        {
            await _run.StopAsync();
        }

        Notify();
    }

    public async Task CloseAsync(string reason)
    {
        var run = _run;
        if (run is not null && !run.IsFinished)
        {
            await run.StopAsync(reason);
        }

        Notify();
    }

    public void RecordObservation(string observation, string? surface = null)
    {
        if (IsRunning || Current is null)
        {
            throw new InvalidOperationException("Finish a test before recording your observation.");
        }

        void Update(DiagnosticSession s)
        {
            s.Observation = observation;
            s.ObservationSurface = surface;
            s.Findings = DiagnosticAnalyzer.Analyze(s);
        }
        _run!.Recorder.Update(Update);
        Notify();
    }

    public bool TryLock(out string? error)
    {
        if (CurrentForDisplay?.Phase != "Waiting for lock")
        {
            error = "Wait until the GIF is applied and the test is ready to lock.";
            return false;
        }
        var result = _windows.TryLock(out error);
        _run?.Recorder.Add(
            "User action",
            result ? "Lock requested; awaiting Windows session notification." : error!,
            result ? "Info" : "Warning"
        );
        return result;
    }

    public async Task<string> ExportAsync(string destinationPath)
    {
        var session = Current ?? throw new InvalidOperationException("Start a test first.");
        await DiagnosticReportWriter.ExportAsync(session, destinationPath);
        return destinationPath;
    }

    public string CreateSummary() =>
        Current is { } session ? DiagnosticReportWriter.CreateSummary(session) : "No diagnostic session is available.";

    public async Task RefreshReadinessAsync()
    {
        var checks = await Task.Run(() =>
        {
            var result = new List<DiagnosticCheck>
            {
                new(
                    "Session monitoring",
                    _windows.IsRegistered ? "Ready" : "Unavailable",
                    _windows.Error ?? "Lock and unlock events for this Windows session."
                ),
                new(
                    "Display-power monitoring",
                    _windows.PowerNotificationsAvailable ? "Ready" : "Unavailable",
                    _windows.PowerError ?? "Display power state notifications are registered."
                ),
                new(
                    "GIF source",
                    _lockscreen.CurrentImage is not null ? "Ready" : "Attention",
                    _lockscreen.CurrentImage is not null
                        ? "Selected GIF available; full inspection runs before applying."
                        : "Select a GIF on the Lockscreen page or use the reference animation."
                ),
                new("Windows API step", "Optional", "Off by default. API-on changes Windows image state before replacing cached files."),
                new(
                    "Process tracing",
                    "Starts with test",
                    "Records relevant file activity during the test. Windows may request administrator permission once."
                ),
            };
            try
            {
                _ = Directory.EnumerateFileSystemEntries(_lockscreen.CacheDirectory).Take(1).ToArray();
                result.Add(
                    new("Cache inventory", "Ready", "Directory can be enumerated. Individual file access is checked during the test.")
                );
            }
            catch (Exception ex)
            {
                result.Add(
                    new("Cache inventory", "Unavailable", $"{ex.GetType().Name}. No permissions were changed; applying may request access.")
                );
            }
            return result;
        });
        _readiness = checks;
        Notify();
    }

    public void Interrupt(string reason)
    {
        if (IsRunning)
        {
            _run?.Interrupt(reason);
        }
    }

    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);
}
