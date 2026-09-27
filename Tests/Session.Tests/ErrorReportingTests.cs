using System.Collections.Concurrent;
using System.ComponentModel;
using LockscreenGif.Contracts.Services;
using LockscreenGif.Services.Analytics;
using LockscreenGif.Services.Diagnostics;

namespace Session.Tests;

internal static class ErrorReportingTests
{
    public static async Task RunAsync(string root)
    {
        await PreparationFailureAsync(root);
        await TraceFailuresAsync();
        await CancellationAsync(root);
        await VerificationFailuresAsync();
        await ReporterFailureAsync(root);
    }

    private static async Task PreparationFailureAsync(string root)
    {
        var reporter = new FakeErrorReporter();
        await using var context = await TestContext.CreateAsync(root, "reported-preparation", reporter);
        context.Lockscreen.CurrentImage = new(Path.Combine(context.DirectoryPath, "missing.gif"));
        await context.Service.StartAsync(false, false);
        var error = reporter.Errors.Single();
        Program.Check(
            error.Exception is FileNotFoundException
                && error.Context == AnalyticsErrorContext.DiagnosticRun
                && error.Workflow == AnalyticsWorkflow.Diagnostics,
            "Diagnostic preparation reports the original exception with diagnostic workflow"
        );
        Program.Check(!context.Service.IsRunning && context.Helper.Disposed, "Reported preparation failures still finish and dispose");
    }

    private static async Task TraceFailuresAsync()
    {
        foreach (var workflow in new[] { AnalyticsWorkflow.Diagnostics, AnalyticsWorkflow.Lockscreen })
        {
            var reporter = new FakeErrorReporter();
            var failure = new IOException("Synthetic trace startup read failure");
            var helper = new FakePrivilegedSession { ReadError = failure };
            var trace = new DiagnosticProcessTrace(helper, new(new()), errorReporter: reporter, workflow: workflow);
            await trace.StartAsync(new("cache", "source.gif"), CancellationToken.None);
            await trace.FinishAsync();
            var error = reporter.Errors.Single();
            Program.Check(
                ReferenceEquals(error.Exception, failure)
                    && error.Context == AnalyticsErrorContext.DiagnosticTrace
                    && error.Workflow == workflow,
                $"Trace startup plus final-drain failure reports once with {workflow} workflow"
            );
        }

        var disconnectReporter = new FakeErrorReporter();
        var disconnectHelper = new FakePrivilegedSession();
        var poll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new IOException("Synthetic polling disconnect");
        var disconnectTrace = new DiagnosticProcessTrace(
            disconnectHelper,
            new(new()),
            waitForPoll: token => poll.Task.WaitAsync(token),
            errorReporter: disconnectReporter
        );
        await disconnectTrace.StartAsync(new("cache", "source.gif"), CancellationToken.None);
        disconnectHelper.ReadError = disconnected;
        poll.SetResult();
        await Program.WaitUntilAsync(() => !disconnectReporter.Errors.IsEmpty, "Trace disconnection was not reported");
        await disconnectTrace.FinishAsync();
        Program.Check(
            disconnectReporter.Errors.Count == 1 && ReferenceEquals(disconnectReporter.Errors.Single().Exception, disconnected),
            "A polling disconnect and its cleanup failure produce one report"
        );

        var drainReporter = new FakeErrorReporter();
        var drainFailure = new IOException("Synthetic drain failure");
        var drainHelper = new FakePrivilegedSession { StopError = drainFailure };
        var drainTrace = new DiagnosticProcessTrace(drainHelper, new(new()), errorReporter: drainReporter);
        await drainTrace.StartAsync(new("cache", "source.gif"), CancellationToken.None);
        await drainTrace.FinishAsync();
        Program.Check(
            ReferenceEquals(drainReporter.Errors.Single().Exception, drainFailure),
            "A final trace drain failure reports the original exception"
        );

        var timeoutReporter = new FakeErrorReporter();
        var timeoutTrace = new DiagnosticProcessTrace(
            new FakePrivilegedSession { StopDelay = TimeSpan.FromSeconds(1) },
            new(new()),
            errorReporter: timeoutReporter,
            finalDrainLimit: TimeSpan.FromMilliseconds(20)
        );
        await timeoutTrace.StartAsync(new("cache", "source.gif"), CancellationToken.None);
        await timeoutTrace.FinishAsync();
        Program.Check(
            timeoutReporter.Errors.Single().Exception is TimeoutException { InnerException: OperationCanceledException },
            "An internal final-drain deadline reports a timeout rather than user cancellation"
        );
    }

    private static async Task CancellationAsync(string root)
    {
        var reporter = new FakeErrorReporter();
        await using (var context = await TestContext.CreateAsync(root, "unreported-decline", reporter))
        {
            context.Helper.Decline = true;
            await context.Service.StartAsync(false, false);
            await context.Service.StopAsync();
            Program.Check(reporter.Errors.IsEmpty, "UAC decline and diagnostic stop are not reported as errors");
        }

        foreach (var failure in new Exception[] { new OperationCanceledException(), new Win32Exception(1223) })
        {
            var trace = new DiagnosticProcessTrace(new FakePrivilegedSession { StartError = failure }, new(new()), errorReporter: reporter);
            await trace.StartAsync(new("cache", "source.gif"), CancellationToken.None);
            await trace.FinishAsync();
        }
        Program.Check(reporter.Errors.IsEmpty, "Trace cancellation is excluded even when the caller token is not cancelled");

        var cancelledDrain = new DiagnosticProcessTrace(
            new FakePrivilegedSession { StopError = new OperationCanceledException() },
            new(new()),
            errorReporter: reporter
        );
        await cancelledDrain.StartAsync(new("cache", "source.gif"), CancellationToken.None);
        await cancelledDrain.FinishAsync();
        Program.Check(reporter.Errors.IsEmpty, "A cancelled final drain before its deadline does not report an error");
    }

    private static async Task VerificationFailuresAsync()
    {
        var reporter = new FakeErrorReporter();
        var windows = new WindowsSessionMonitor { LockSucceeds = false };
        var failure = new IOException("Synthetic helper creation failure");
        var service = new LockscreenVerificationService(
            new FakeLockscreenService("cache"),
            windows,
            new(_ => throw failure),
            errorReporter: reporter
        );
        var result = await service.VerifyAndLockAsync(new() { Success = true }, "source.gif");
        var error = reporter.Errors.Single();
        Program.Check(
            ReferenceEquals(error.Exception, failure)
                && error.Context == AnalyticsErrorContext.LockscreenVerification
                && error.Workflow == AnalyticsWorkflow.Lockscreen
                && !result.ReadConfirmed,
            "Regular verification helper failures report normal workflow without claiming a failed apply"
        );

        reporter = new();
        var disposeFailure = new IOException("Synthetic helper shutdown failure");
        var helper = new FakePrivilegedSession { DisposeError = disposeFailure };
        service = new(new FakeLockscreenService("cache"), windows, new(_ => helper), errorReporter: reporter);
        await service.VerifyAndLockAsync(new() { Success = true }, "source.gif");
        Program.Check(
            ReferenceEquals(reporter.Errors.Single().Exception, disposeFailure) && helper.Disposed,
            "Verification helper shutdown failures are reported while verification still finishes"
        );
    }

    private static async Task ReporterFailureAsync(string root)
    {
        var reporter = new FakeErrorReporter { Throw = true };
        await using (var context = await TestContext.CreateAsync(root, "broken-reporter", reporter))
        {
            context.Lockscreen.CurrentImage = new(Path.Combine(context.DirectoryPath, "missing.gif"));
            await context.Service.StartAsync(false, false);
            Program.Check(
                !context.Service.IsRunning && context.Helper.Disposed && reporter.Errors.Count == 1,
                "A throwing error reporter cannot prevent diagnostic failure cleanup"
            );
        }

        var helper = new FakePrivilegedSession { Disconnect = true };
        var trace = new DiagnosticProcessTrace(helper, new(new()), errorReporter: reporter);
        await trace.StartAsync(new("cache", "source.gif"), CancellationToken.None);
        await trace.FinishAsync();
        Program.Check(helper.Stops == 1 && reporter.Errors.Count == 2, "A throwing reporter cannot prevent final trace shutdown");
    }

    private sealed class FakeErrorReporter : IErrorReporter
    {
        public ConcurrentQueue<(Exception Exception, AnalyticsErrorContext Context, AnalyticsWorkflow Workflow)> Errors { get; } = new();
        public bool Throw { get; init; }

        public void CaptureException(Exception exception, AnalyticsErrorContext context, AnalyticsWorkflow workflow)
        {
            Errors.Enqueue((exception, context, workflow));
            if (Throw)
            {
                throw new InvalidOperationException("Synthetic reporter fault");
            }
        }
    }
}
