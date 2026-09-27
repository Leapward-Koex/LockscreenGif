using System.Diagnostics;
using System.Threading.Channels;
using LockscreenGif.Contracts.Services;
using LockscreenGif.Models;
using LockscreenGif.Models.Diagnostics;
using LockscreenGif.Privileged;
using LockscreenGif.Services.Analytics;

namespace LockscreenGif.Services.Diagnostics;

/// <summary>Owns one apply/lock/unlock lifetime, including cancellation and final reconciliation.</summary>
internal sealed class DiagnosticRun
{
    private readonly ILockscreenService _lockscreen;
    private readonly WindowsSessionMonitor _windows;
    private readonly CancellationTokenSource _stop = new();

    // These are coalesced wake-up hints only. Every native observation is separately recorded;
    // critical fresh-read requests survive coalescing in _forceNextSnapshot.
    private readonly Channel<string> _requests = Channel.CreateBounded<string>(
        new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest }
    );
    private readonly CacheCollector _cache;
    private readonly string _source;
    private readonly LockscreenSourceKind _sourceKind;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly TimeSpan _limit;
    private Task? _monitorTask;
    private Task? _startTask;
    private string? _interruptionReason;
    private bool _ready;
    private int _forceNextSnapshot;
    private readonly object _finishGate = new();
    private Task? _finishTask;
    private readonly IPrivilegedOperationSession _privileged;
    private readonly DiagnosticProcessTrace _trace;
    private readonly IErrorReporter? _errorReporter;
    public DiagnosticRecorder Recorder { get; }
    public bool IsFinished { get; private set; }
    public event Action<DiagnosticRun>? Finished;

    public DiagnosticRun(
        ILockscreenService lockscreen,
        WindowsSessionMonitor windows,
        DiagnosticSession session,
        string source,
        IPrivilegedOperationSession privileged,
        TimeSpan? testLimit = null,
        LockscreenSourceKind sourceKind = LockscreenSourceKind.Unknown,
        IErrorReporter? errorReporter = null
    )
    {
        _lockscreen = lockscreen;
        _windows = windows;
        _source = source;
        _sourceKind = sourceKind;
        _limit = testLimit ?? TimeSpan.FromMinutes(5);
        _stop.CancelAfter(_limit);
        Recorder = new(session);
        _privileged = privileged;
        _errorReporter = errorReporter;
        _trace = new(privileged, Recorder, errorReporter: errorReporter);
        _cache = new(
            lockscreen.CacheDirectory,
            (message, severity) =>
            {
                Recorder.Add("Cache event", message, severity);
                if (severity == "Warning")
                {
                    Recorder.Update(s => s.MonitoringComplete = false);
                }
            },
            () => _requests.Writer.TryWrite("File changes")
        );
    }

    public Task StartAsync() => IsFinished ? Task.CompletedTask : _startTask ??= StartCoreAsync();

    private async Task StartCoreAsync()
    {
        // Establish _startTask before callbacks can request cancellation during startup.
        await Task.Yield();
        if (IsFinished)
        {
            return;
        }

        _windows.Observed += OnWindowsEvent;
        string? referenceDirectory = null;
        try
        {
            Recorder.Phase("Preparing");
            Recorder.Update(s => s.MonitoringComplete = _windows.IsRegistered && _windows.PowerNotificationsAvailable);
            if (!_windows.IsRegistered)
            {
                Recorder.Add("Collector", _windows.Error ?? "Lock/unlock monitoring is unavailable.", "Warning");
            }

            if (!_windows.PowerNotificationsAvailable)
            {
                Recorder.Add("Collector", _windows.PowerError ?? "Display-power monitoring is unavailable.", "Warning");
            }

            var session = Recorder.Snapshot(includeTraceDetails: false);
            var original = _source;
            var sourceKind = _sourceKind;
            if (session.UseReference)
            {
                referenceDirectory = Path.Combine(Path.GetTempPath(), "LockscreenGif-reference-" + Guid.NewGuid().ToString("N"));
                original = await ReferenceAnimation.EnsureAsync(referenceDirectory);
                sourceKind = LockscreenSourceKind.BundledGif;
            }
            if (string.IsNullOrWhiteSpace(original))
            {
                throw new InvalidOperationException("Select a GIF on the Lockscreen page or use the reference animation.");
            }
            // Keep the selected bytes stable through inspection and applying without retaining a copy.
            using var sourceLease = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read);
            Recorder.Update(s =>
            {
                s.SourcePath = original;
                s.SourceName = Path.GetFileName(original);
            });
            var gif = await Task.Run(() => GifInspector.InspectAsync(original, _stop.Token), _stop.Token);
            Recorder.Update(s => s.Gif = gif);
            if (gif.Error is not null || !gif.IsAnimated)
            {
                throw new InvalidDataException(gif.Error ?? "The selected GIF has fewer than two frames. Select an animated GIF.");
            }

            Recorder.Add("Source", $"{gif.Width} × {gif.Height}, {gif.FrameCount} frames, {gif.SizeBytes:N0} bytes; SHA-256 {gif.Sha256}.");
            await _trace.StartAsync(new(_lockscreen.CacheDirectory, original), _stop.Token);
            _cache.Start();
            await CaptureAsync("Baseline", true);
            Recorder.Phase("Applying");
            var result = await _lockscreen.ApplyAsync(
                original,
                session.UseWindowsApi,
                e => Recorder.Add("Apply: " + e.Stage, e.Message + (e.Path is null ? "" : $" — {e.Path}"), e.Severity),
                _stop.Token,
                _privileged,
                sourceKind
            );
            if (IsFinished)
            {
                return;
            }

            Recorder.Update(s => s.ApplyResult = result);
            await CaptureAsync("AfterApply", true);
            if (!result.Success)
            {
                Recorder.Update(s => s.Error = result.Error ?? "Not every destination was verified.");
                await FinishAsync(result.Cancelled ? "Cancelled" : "Results");
                return;
            }
            _ready = true;
            Recorder.Phase("Waiting for lock");
            Recorder.Add(
                "Instructions",
                "Check whether the GIF animates, then unlock when you're ready. Monitoring continues for up to five minutes."
            );
            _monitorTask = MonitorAsync();
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(CancellationPhase);
        }
        catch (Exception ex)
        {
            ReportError(ex);
            Recorder.Update(s => s.Error = $"{ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}");
            await FinishAsync("Results");
        }
        finally
        {
            if (referenceDirectory is not null)
            {
                try
                {
                    File.Delete(Path.Combine(referenceDirectory, "diagnostics-reference-v1.gif"));
                    Directory.Delete(referenceDirectory);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Temporary reference cleanup failed: {ex.GetType().Name}");
                }
            }
        }
    }

    private void OnWindowsEvent(string name)
    {
        Recorder.Add("Windows session", name);
        if (name is "SessionLock" or "SystemResume" or "DisplayConfigurationChanged")
        {
            Interlocked.Exchange(ref _forceNextSnapshot, 1);
        }

        if (name == "SessionLock")
        {
            if (!_ready)
            {
                Recorder.Add("Session", "Screen locked before applying completed; unlock and repeat the lock step.", "Warning");
                return;
            }
            Recorder.Update(s =>
            {
                s.LockObserved = true;
                s.Phase = "Locked";
            });
        }
        else if (name == "SessionUnlock" && _ready && Recorder.Snapshot(includeTraceDetails: false).LockObserved)
        {
            Recorder.Update(s =>
            {
                s.UnlockObserved = true;
                s.Phase = "Collecting final evidence";
            });
        }
        else if (name is "SystemSuspend" or "ConsoleDisconnected" or "RemoteDisconnected")
        {
            Recorder.Update(s => s.MonitoringComplete = false);
        }

        _requests.Writer.TryWrite(name);
    }

    private async Task MonitorAsync()
    {
        try
        {
            while (true)
            {
                _stop.Token.ThrowIfCancellationRequested();
                if (_elapsed.Elapsed >= TimeSpan.FromMinutes(5))
                {
                    await FinishAsync("Timed out");
                    return;
                }
                if (Recorder.Snapshot(includeTraceDetails: false).UnlockObserved)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), _stop.Token);
                    await CaptureAsync("AfterUnlock", true);
                    await FinishAsync("Results");
                    return;
                }
                var reason = "Reconciliation";
                using var interval = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                interval.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    reason = await _requests.Reader.ReadAsync(interval.Token);
                }
                catch (OperationCanceledException) when (!_stop.IsCancellationRequested) { }
                // Coalesce noisy file events, keeping native transitions in the event timeline.
                await Task.Delay(250, _stop.Token);
                while (_requests.Reader.TryRead(out var next))
                {
                    if (reason == "File changes")
                    {
                        reason = next;
                    }
                }

                await CaptureAsync(reason, Interlocked.Exchange(ref _forceNextSnapshot, 0) != 0);
            }
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(CancellationPhase);
        }
        catch (Exception ex)
        {
            ReportError(ex);
            Recorder.Update(s =>
            {
                s.MonitoringComplete = false;
                s.Error = $"Monitoring failed: {ex.GetType().Name}: {ex.Message}";
            });
            await FinishAsync("Interrupted");
        }
    }

    private async Task CaptureAsync(string reason, bool force)
    {
        if (reason is "Baseline" or "AfterApply" or "AfterUnlock" or "SystemResume" or "DisplayConfigurationChanged")
        {
            var environment = await Task.Run(() => EnvironmentCollector.Collect(_lockscreen.CacheDirectory), _stop.Token);
            environment["Session notifications"] = _windows.IsRegistered ? "Registered" : _windows.Error ?? "Unavailable";
            environment["Display-power notifications"] = _windows.PowerNotificationsAvailable
                ? "Registered"
                : _windows.PowerError ?? "Unavailable";
            var environmentTruncated = false;
            Recorder.Update(s =>
            {
                if (reason == "Baseline")
                {
                    s.Environment = environment;
                }

                s.EnvironmentObservations.Add(new(DateTimeOffset.UtcNow, reason, environment));
                environmentTruncated = DiagnosticSnapshotRetention.Enforce(s);
            });
            if (environmentTruncated)
            {
                Recorder.Add("Collector", DiagnosticSnapshotRetention.WarningMessage, "Warning");
            }
        }
        var snapshot = await _cache.CaptureAsync(reason, force, _stop.Token);
        var snapshotsTruncated = false;
        Recorder.Update(s =>
        {
            s.Snapshots.Add(snapshot);
            snapshotsTruncated = DiagnosticSnapshotRetention.Enforce(s);
        });
        if (snapshotsTruncated)
        {
            Recorder.Add("Collector", DiagnosticSnapshotRetention.WarningMessage, "Warning");
        }

        Recorder.Add("Snapshot", $"{reason}: {snapshot.Files.Count} files; {(snapshot.Complete ? "complete" : "incomplete")}.");
    }

    private string CancellationPhase =>
        _interruptionReason is not null ? "Interrupted"
        : _elapsed.Elapsed >= _limit ? "Timed out"
        : "Cancelled";

    public async Task StopAsync(string? interruptionReason = null)
    {
        if (IsFinished)
        {
            return;
        }

        if (interruptionReason is not null)
        {
            _interruptionReason = interruptionReason;
            Recorder.Update(s =>
            {
                s.Error = interruptionReason;
                s.MonitoringComplete = false;
            });
        }
        _stop.Cancel();
        // Await preparation/applying as well as monitoring. Native image setting and
        // permission commands may require time to reach a safe cancellation boundary.
        if (_startTask is not null)
        {
            await _startTask;
        }

        if (_monitorTask is not null)
        {
            await _monitorTask;
        }

        await FinishAsync(CancellationPhase);
    }

    public void Interrupt(string message)
    {
        if (IsFinished)
        {
            return;
        }

        _interruptionReason = message;
        _stop.Cancel();
        Recorder.Update(s =>
        {
            s.Error = message;
            s.MonitoringComplete = false;
            s.Phase = "Interrupted";
        });
        _ = StopAsync(message);
    }

    private Task FinishAsync(string phase)
    {
        lock (_finishGate)
        {
            return _finishTask ??= FinishCoreAsync(phase);
        }
    }

    private async Task FinishCoreAsync(string phase)
    {
        await Task.Yield();
        _ready = false;
        _windows.Observed -= OnWindowsEvent;
        _cache.Dispose();
        try
        {
            await _trace.FinishAsync();
        }
        finally
        {
            try
            {
                await _privileged.DisposeAsync();
            }
            catch (Exception ex)
            {
                ReportError(ex);
                Recorder.Update(s =>
                {
                    s.ProcessTrace.State = "Incomplete";
                    s.ProcessTrace.Reason = $"Helper shutdown failed: {ex.GetType().Name}.";
                });
            }
        }
        if (_interruptionReason is not null)
        {
            phase = "Interrupted";
        }

        Recorder.Update(s =>
        {
            s.Phase = phase;
            s.EndedAt = DateTimeOffset.UtcNow;
            s.Findings = DiagnosticAnalyzer.Analyze(s);
        });
        IsFinished = true;
        Finished?.Invoke(this);
    }

    private void ReportError(Exception exception) =>
        DiagnosticErrorReporting.Capture(_errorReporter, exception, AnalyticsErrorContext.DiagnosticRun, AnalyticsWorkflow.Diagnostics);
}
