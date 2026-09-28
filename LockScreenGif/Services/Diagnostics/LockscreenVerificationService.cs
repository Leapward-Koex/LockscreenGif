using LockscreenGif.Contracts.Services;
using LockscreenGif.Models;
using LockscreenGif.Models.Diagnostics;
using LockscreenGif.Privileged;
using LockscreenGif.Services.Analytics;

namespace LockscreenGif.Services.Diagnostics;

/// <summary>Checks the next lock cycle without applying the GIF again or retaining a diagnostic report.</summary>
public sealed class LockscreenVerificationService
{
    private readonly ILockscreenService _lockscreen;
    private readonly WindowsSessionMonitor _windows;
    private readonly PrivilegedSessionFactory _privileged;
    private readonly TimeSpan _monitorLimit;
    private readonly TimeSpan _unlockDelay;
    private readonly IErrorReporter? _errorReporter;
    private readonly object _gate = new();
    private CancellationTokenSource? _stop;
    private Task<LockscreenVerificationResult>? _run;
    private int _running;

    public LockscreenVerificationService(
        ILockscreenService lockscreen,
        WindowsSessionMonitor windows,
        PrivilegedSessionFactory privileged,
        IErrorReporter? errorReporter = null
    )
        : this(lockscreen, windows, privileged, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1), errorReporter) { }

    internal LockscreenVerificationService(
        ILockscreenService lockscreen,
        WindowsSessionMonitor windows,
        PrivilegedSessionFactory privileged,
        TimeSpan monitorLimit,
        TimeSpan unlockDelay,
        IErrorReporter? errorReporter = null
    )
    {
        _lockscreen = lockscreen;
        _windows = windows;
        _privileged = privileged;
        _monitorLimit = monitorLimit;
        _unlockDelay = unlockDelay;
        _errorReporter = errorReporter;
    }

    public bool IsRunning => Volatile.Read(ref _running) != 0;
    public string Status { get; private set; } = "";
    public event EventHandler? Changed;

    public Task<LockscreenVerificationResult> VerifyAndLockAsync(LockscreenApplyResult apply, string sourcePath)
    {
        lock (_gate)
        {
            if (IsRunning || _lockscreen.IsApplying)
            {
                throw new InvalidOperationException("Wait for the current lock-screen operation to finish.");
            }

            Volatile.Write(ref _running, 1);
            _stop = new();
            _run = RunAsync(apply, sourcePath, _stop.Token);
            return _run;
        }
    }

    public async Task CloseAsync()
    {
        Task<LockscreenVerificationResult>? run;
        lock (_gate)
        {
            _stop?.Cancel();
            run = _run;
        }

        if (run is not null)
        {
            await run;
        }
    }

    private async Task<LockscreenVerificationResult> RunAsync(LockscreenApplyResult apply, string sourcePath, CancellationToken token)
    {
        // Publish the task before a notification can initiate shutdown.
        await Task.Yield();
        try
        {
            SetStatus("Preparing file-read check…");
            token.ThrowIfCancellationRequested();
            if (!apply.Success)
            {
                return Warning(false, "The GIF was not fully applied. Apply it successfully before checking the lock screen.");
            }

            if (!_windows.IsRegistered)
            {
                return Warning(
                    false,
                    "The GIF was applied, but lock and unlock monitoring is unavailable. You can lock manually with Win+L."
                );
            }

            return await MonitorAsync(apply, sourcePath, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return Warning(false, "The lock-screen check was cancelled. The GIF was applied.");
        }
        catch (Exception ex)
        {
            ReportError(ex);
            Logger.Warn($"Lock-screen verification failed: {ex.GetType().Name}: {ex.Message}");
            return Warning(false, "The GIF was applied, but its lock-screen read could not be confirmed.");
        }
        finally
        {
            lock (_gate)
            {
                _stop?.Dispose();
                _stop = null;
                Volatile.Write(ref _running, 0);
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<LockscreenVerificationResult> MonitorAsync(LockscreenApplyResult apply, string sourcePath, CancellationToken token)
    {
        var recorder = new DiagnosticRecorder(new DiagnosticSession { ApplyResult = apply });
        var sessionGate = new object();
        var unlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lockObserved = false;
        var timedOut = false;
        IPrivilegedOperationSession? helper = null;
        DiagnosticProcessTrace? trace = null;

        void Observe(string name)
        {
            string? status = null;
            lock (sessionGate)
            {
                if (name == "SessionLock")
                {
                    lockObserved = true;
                    if (unlocked.Task.IsCompleted)
                    {
                        unlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    status = "Waiting for unlock…";
                }
                else if (name == "SessionUnlock" && lockObserved)
                {
                    unlocked.TrySetResult();
                    status = "Finishing file-read check…";
                }
            }

            if (status is not null)
            {
                SetStatus(status);
            }
        }

        bool LockWasObserved()
        {
            lock (sessionGate)
            {
                return lockObserved;
            }
        }

        Task UnlockTask()
        {
            lock (sessionGate)
            {
                return unlocked.Task;
            }
        }

        async Task FinishTraceAsync()
        {
            if (trace is not null)
            {
                await trace.FinishAsync();
            }

            if (helper is not null)
            {
                var owned = helper;
                helper = null;
                try
                {
                    await owned.DisposeAsync();
                }
                catch (Exception ex)
                {
                    ReportError(ex);
                    Logger.Warn($"Lock-screen verification helper shutdown failed: {ex.GetType().Name}.");
                    recorder.Update(session => session.ProcessTrace.State = "Incomplete");
                }
            }
        }

        _windows.Observed += Observe;
        try
        {
            try
            {
                helper = _privileged.Create(_lockscreen.CacheDirectory);
                trace = new(helper, recorder, errorReporter: _errorReporter, workflow: AnalyticsWorkflow.Lockscreen);
                await trace.StartAsync(new(_lockscreen.CacheDirectory, sourcePath), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                ReportError(ex);
                Logger.Warn($"Lock-screen file monitoring could not start: {ex.GetType().Name}.");
                recorder.Update(session => session.ProcessTrace.State = "Unavailable");
            }

            token.ThrowIfCancellationRequested();
            if (!LockWasObserved() && !_windows.TryLock(out var error))
            {
                Logger.Warn(error ?? "Windows could not request locking.");
                return Warning(false, "The GIF was applied, but Windows could not lock the screen. You can try Win+L when you're ready.");
            }

            SetStatus(UnlockTask().IsCompleted ? "Finishing file-read check…" : "Waiting for unlock…");

            try
            {
                await UnlockTask().WaitAsync(_monitorLimit, token);
                await Task.Delay(_unlockDelay, token);
            }
            catch (TimeoutException)
            {
                timedOut = true;
            }

            await FinishTraceAsync();
            if (timedOut)
            {
                if (!LockWasObserved())
                {
                    return Warning(false, "Windows did not report a lock and unlock cycle. The GIF was applied.");
                }

                // The elevated trace is already stopped. Keep only this lightweight listener
                // so a timeout cannot surface a result or toast while the desktop is locked.
                SetStatus("File monitoring has ended. Waiting for unlock…");
            }

            // A quick re-lock during the final delay or helper shutdown needs its own
            // unlock before feedback is delivered. Collection remains stopped.
            await UnlockTask().WaitAsync(token);

            return LockscreenReadVerification.CreateResult(recorder.Snapshot(), _windows.SessionId, timedOut);
        }
        finally
        {
            _windows.Observed -= Observe;
            await FinishTraceAsync();
        }
    }

    private static LockscreenVerificationResult Warning(bool unlocked, string message) =>
        new(false, unlocked, "Lock-screen read not confirmed", message);

    private void ReportError(Exception exception) =>
        DiagnosticErrorReporting.Capture(
            _errorReporter,
            exception,
            AnalyticsErrorContext.LockscreenVerification,
            AnalyticsWorkflow.Lockscreen
        );

    private void SetStatus(string status)
    {
        Status = status;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
