using LockscreenGif.Contracts.Services;
using LockscreenGif.Models;
using LockscreenGif.Models.Diagnostics;
using LockscreenGif.Privileged;
using LockscreenGif.Services.Diagnostics;

namespace Session.Tests;

internal static class LockscreenVerificationTests
{
    private const string Target = "C:\\cache\\LockScreen.jpg";
    private static readonly DateTimeOffset VerifiedAt = DateTimeOffset.UtcNow.AddMinutes(-1);

    public static async Task RunAsync()
    {
        EvidenceChecks();
        await CompletedCycleAsync();
        await StartupAndShutdownAsync();
        await RelockDuringDrainAsync();
        await TimeoutAsync();
        await UnavailableAsync();
    }

    private static LockscreenApplyResult Applied() =>
        new()
        {
            Success = true,
            Files =
            [
                new()
                {
                    Path = Target,
                    Copied = true,
                    Verified = true,
                    VerifiedAt = VerifiedAt,
                },
            ],
        };

    private static TraceAggregate Read() =>
        new()
        {
            Path = Target,
            ProcessId = 500,
            ProcessInstance = 6,
            ProcessName = "LogonUI.exe",
            SessionId = 1,
            AttributionResolved = true,
            Reads = 1,
            ReadBytes = 512,
            LastReadStartedAt = VerifiedAt.AddSeconds(1),
            LastReadCompletedAt = VerifiedAt.AddSeconds(2),
        };

    private static void EvidenceChecks()
    {
        var reader = Read();
        var session = new DiagnosticSession
        {
            ApplyResult = Applied(),
            ProcessTrace = new() { State = "Completed", Files = [reader] },
        };
        Program.Check(LockscreenReadVerification.HasConfirmedRead(session, 1), "LogonUI read after verification confirms the applied file");
        reader.ProcessName = "LogonUI";
        Program.Check(
            LockscreenReadVerification.HasConfirmedRead(session, 1),
            "Startup process snapshots without an exe suffix identify LogonUI"
        );
        reader.ProcessName = "LogonUI-other.exe";
        Program.Check(!LockscreenReadVerification.HasConfirmedRead(session, 1), "A process-name prefix does not identify LogonUI");
        reader.ProcessName = "Other.exe";
        Program.Check(!LockscreenReadVerification.HasConfirmedRead(session, 1), "An unrelated application cannot confirm LogonUI access");
        reader.ProcessName = "LOGONUI.EXE";
        reader.SessionId = 2;
        Program.Check(
            !LockscreenReadVerification.HasConfirmedRead(session, 1),
            "A different Windows session cannot confirm the current lock screen"
        );
        reader.SessionId = null;
        Program.Check(!LockscreenReadVerification.HasConfirmedRead(session, 1), "Missing process session remains inconclusive");
        reader.SessionId = 1;
        reader.IsApp = true;
        Program.Check(!LockscreenReadVerification.HasConfirmedRead(session, 1), "App-attributed activity cannot confirm LogonUI access");
        reader.IsApp = false;
        reader.AttributionResolved = false;
        Program.Check(!LockscreenReadVerification.HasConfirmedRead(session, 1), "Unresolved attribution cannot confirm LogonUI access");
        reader.AttributionResolved = true;
        reader.ProcessId = 4;
        Program.Check(!LockscreenReadVerification.HasConfirmedRead(session, 1), "System activity cannot confirm LogonUI access");
        reader.ProcessId = 500;
        reader.LastReadStartedAt = VerifiedAt.AddSeconds(-1);
        Program.Check(
            !LockscreenReadVerification.HasConfirmedRead(session, 1),
            "A read started before copying remains inconclusive after completion"
        );
        reader.LastReadStartedAt = VerifiedAt.AddSeconds(1);
        session.ApplyResult.Files[0].Verified = false;
        Program.Check(!LockscreenReadVerification.HasConfirmedRead(session, 1), "An unverified copy cannot pass the regular read check");
        session.ApplyResult.Files[0].Verified = true;
        session.ProcessTrace.QueueDropped = 1;
        Program.Check(
            LockscreenReadVerification.CreateResult(session, 1, true).ReadConfirmed,
            "A positive LogonUI read survives timeout and collection gaps"
        );
        session.ProcessTrace.Files.Clear();
        var operation = new TraceOperation(
            VerifiedAt.AddSeconds(1),
            Target,
            "Read",
            500,
            6,
            "LogonUI.exe",
            1,
            false,
            true,
            512,
            512,
            0,
            CompletedAt: VerifiedAt.AddSeconds(2)
        );
        session.ProcessTrace.Operations = [operation];
        Program.Check(LockscreenReadVerification.HasConfirmedRead(session, 1), "Retained successful reads survive omitted aggregates");
        session.ProcessTrace.Operations = [operation with { CompletedBytes = 0 }];
        Program.Check(!LockscreenReadVerification.HasConfirmedRead(session, 1), "Zero-byte raw reads cannot confirm access");
        session.ProcessTrace.Operations = [operation with { Status = 0x103 }];
        Program.Check(!LockscreenReadVerification.HasConfirmedRead(session, 1), "Pending raw reads cannot confirm access");
        session.ProcessTrace.Operations = [operation with { CompletedAt = null }];
        Program.Check(!LockscreenReadVerification.HasConfirmedRead(session, 1), "Uncompleted raw reads cannot confirm access");
        session.ProcessTrace.Operations.Clear();
        session.ProcessTrace.QueueDropped = 0;
        var missing = LockscreenReadVerification.CreateResult(session, 1, false);
        Program.Check(
            !missing.ReadConfirmed && missing.Message.Contains("may have reused"),
            "Missing reads explain caching without claiming failure"
        );
    }

    private static (
        LockscreenVerificationService Service,
        WindowsSessionMonitor Windows,
        FakePrivilegedSession Helper,
        FakeLockscreenService Lockscreen
    ) Setup(TimeSpan? limit = null)
    {
        var windows = new WindowsSessionMonitor();
        var helper = new FakePrivilegedSession();
        var lockscreen = new FakeLockscreenService("C:\\cache");
        return (new(lockscreen, windows, new(_ => helper), limit ?? TimeSpan.FromSeconds(5), TimeSpan.Zero), windows, helper, lockscreen);
    }

    private static async Task CompletedCycleAsync()
    {
        var context = Setup();
        context.Helper.EvidenceFactory = stopped => new() { State = stopped ? "Completed" : "Recording", Files = [Read()] };
        var run = context.Service.VerifyAndLockAsync(Applied(), "source.gif");
        await Program.WaitUntilAsync(() => context.Windows.LockRequests == 1, "Read check did not request locking.");
        Program.Check(context.Helper.Starts == 1 && context.Service.IsRunning, "Tracing starts before the regular lock request");
        var diagnostics = new DiagnosticsSessionService(context.Lockscreen, context.Windows, new(_ => context.Helper), context.Service);
        var rejected = false;
        try
        {
            await diagnostics.StartAsync(false, false);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        Program.Check(rejected, "Diagnostics cannot start while regular verification owns the lock cycle");
        context.Windows.Emit("SessionUnlock");
        Program.Check(!run.IsCompleted, "Unlock without an observed lock does not finish regular verification");
        context.Windows.Emit("SessionLock");
        context.Windows.Emit("SessionUnlock");
        var result = await run.WaitAsync(TimeSpan.FromSeconds(3));
        Program.Check(result.ReadConfirmed && result.UnlockObserved, "The observed unlock returns a confirmed LogonUI read");
        Program.Check(
            context.Helper.Stops == 1 && context.Helper.Disposed && !context.Service.IsRunning,
            "Regular verification drains and disposes its helper once"
        );
        Program.Check(context.Lockscreen.Applies.Count == 0, "Regular verification never reapplies the GIF");
    }

    private static async Task StartupAndShutdownAsync()
    {
        var context = Setup();
        context.Helper.StartGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = context.Service.VerifyAndLockAsync(Applied(), "source.gif");
        await Program.WaitUntilAsync(() => context.Helper.Starts == 1, "Trace startup did not begin.");
        Program.Check(context.Windows.LockRequests == 0, "Screen locking waits for trace startup");
        context.Windows.Emit("SessionLock");
        context.Helper.StartGate.SetResult();
        await Program.WaitUntilAsync(() => context.Service.Status == "Waiting for unlock…", "Manual lock was not retained during startup.");
        Program.Check(context.Windows.LockRequests == 0, "A manual lock during trace startup avoids a second lock request");
        context.Windows.Emit("SessionUnlock");
        Program.Check((await run).UnlockObserved, "Manual startup lock pairs with unlock");

        context = Setup();
        context.Helper.StartGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        run = context.Service.VerifyAndLockAsync(Applied(), "source.gif");
        await Program.WaitUntilAsync(() => context.Helper.Starts == 1, "Cancellable trace startup did not begin.");
        await context.Service.CloseAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Program.Check(
            context.Helper.Disposed && context.Windows.LockRequests == 0 && !context.Service.IsRunning,
            "Closing during trace startup cleans up without locking the screen"
        );
        Program.Check(!(await run).UnlockObserved, "Shutdown does not invent an unlock observation");
    }

    private static async Task TimeoutAsync()
    {
        var context = Setup(TimeSpan.FromMilliseconds(150));
        var run = context.Service.VerifyAndLockAsync(Applied(), "source.gif");
        await Program.WaitUntilAsync(() => context.Windows.LockRequests == 1, "Timed check did not request locking.");
        context.Windows.Emit("SessionLock");
        await Program.WaitUntilAsync(() => context.Helper.Disposed, "Timeout did not dispose the trace helper.");
        Program.Check(!run.IsCompleted && context.Service.IsRunning, "Timeout stops tracing while delaying its result until unlock");
        context.Windows.Emit("SessionUnlock");
        var result = await run.WaitAsync(TimeSpan.FromSeconds(3));
        Program.Check(
            result.UnlockObserved && !result.ReadConfirmed && result.Message.Contains("time limit"),
            "Unlock after timeout explains the incomplete read check"
        );

        context = Setup(TimeSpan.FromMilliseconds(150));
        run = context.Service.VerifyAndLockAsync(Applied(), "source.gif");
        var unobserved = await run.WaitAsync(TimeSpan.FromSeconds(3));
        Program.Check(!unobserved.UnlockObserved && context.Helper.Disposed, "Unobserved lock requests end with a bounded warning");

        context = Setup(TimeSpan.FromMilliseconds(150));
        run = context.Service.VerifyAndLockAsync(Applied(), "source.gif");
        await Program.WaitUntilAsync(() => context.Windows.LockRequests == 1, "Shutdown timeout check did not request locking.");
        context.Windows.Emit("SessionLock");
        await Program.WaitUntilAsync(() => context.Helper.Disposed, "Shutdown timeout check did not stop tracing.");
        await context.Service.CloseAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Program.Check(
            !context.Service.IsRunning && !(await run).UnlockObserved,
            "Closing cancels the lightweight unlock wait after tracing has timed out"
        );
    }

    private static async Task RelockDuringDrainAsync()
    {
        var context = Setup();
        context.Helper.StopDelay = TimeSpan.FromMilliseconds(200);
        var run = context.Service.VerifyAndLockAsync(Applied(), "source.gif");
        await Program.WaitUntilAsync(() => context.Windows.LockRequests == 1, "Re-lock check did not request locking.");
        context.Windows.Emit("SessionLock");
        context.Windows.Emit("SessionUnlock");
        await Program.WaitUntilAsync(() => context.Helper.Stops == 1, "Re-lock check did not start draining.");
        context.Windows.Emit("SessionLock");
        await Program.WaitUntilAsync(() => context.Helper.Disposed, "Re-lock check did not finish draining.");
        Program.Check(!run.IsCompleted, "Re-lock during trace draining delays feedback until the latest unlock");
        context.Windows.Emit("SessionUnlock");
        Program.Check(
            (await run.WaitAsync(TimeSpan.FromSeconds(3))).UnlockObserved,
            "The latest unlock releases drained verification feedback"
        );
    }

    private static async Task UnavailableAsync()
    {
        var context = Setup();
        context.Helper.Decline = true;
        var run = context.Service.VerifyAndLockAsync(Applied(), "source.gif");
        await Program.WaitUntilAsync(() => context.Windows.LockRequests == 1, "Unavailable tracing prevented the requested lock.");
        context.Windows.Emit("SessionLock");
        context.Windows.Emit("SessionUnlock");
        var result = await run.WaitAsync(TimeSpan.FromSeconds(3));
        Program.Check(
            result.UnlockObserved && !result.ReadConfirmed && context.Helper.Disposed,
            "Unavailable tracing still locks and reports uncertainty after unlock"
        );

        context = Setup();
        context.Windows.IsRegistered = false;
        result = await context.Service.VerifyAndLockAsync(Applied(), "source.gif");
        Program.Check(
            !result.UnlockObserved && context.Helper.Starts == 0 && context.Windows.LockRequests == 0,
            "Missing session notifications warn without locking or starting a helper"
        );

        context = Setup();
        context.Windows.LockSucceeds = false;
        result = await context.Service.VerifyAndLockAsync(Applied(), "source.gif");
        Program.Check(
            !result.UnlockObserved && context.Helper.Stops == 1 && context.Helper.Disposed,
            "A failed lock request returns a warning and closes monitoring"
        );
    }
}
