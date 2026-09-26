using LockscreenGif.Services.Diagnostics;

namespace Session.Tests;

internal static class TracingLifecycleTests
{
    public static async Task RunAsync(string root)
    {
        await using (var context = await TestContext.CreateAsync(root, "trace-declined"))
        {
            context.Helper.Decline = true;
            await context.Service.StartAsync(false, false);
            Program.Check(
                context.Service.Current!.Phase == "Waiting for lock"
                    && context.Helper.Starts == 1
                    && context.Service.Current.ProcessTrace.State == "Unavailable",
                "Declining trace elevation permits basic applying without a second launch"
            );
            await context.Service.StopAsync();
            Program.Check(context.Helper.Disposed, "Declined helper session is disposed");
        }
        await using (var context = await TestContext.CreateAsync(root, "trace-final-drain"))
        {
            await context.Service.StartAsync(false, false);
            context.Helper.StopDelay = TimeSpan.FromMilliseconds(200);
            var stopping = context.Service.StopAsync();
            await Task.Delay(40);
            Program.Check(!stopping.IsCompleted && context.Service.IsRunning, "Results wait for final trace draining");
            await stopping;
            Program.Check(
                context.Helper.Stops == 1 && context.Helper.Disposed && context.Service.Current!.ProcessTrace.State == "Completed",
                "Trace is drained and disposed exactly once"
            );
        }
        await using (var context = await TestContext.CreateAsync(root, "trace-timeout"))
        {
            var run = new DiagnosticRun(
                context.Lockscreen,
                context.Windows,
                new(),
                context.Lockscreen.CurrentImage!.Path,
                context.Helper,
                TimeSpan.FromMilliseconds(500)
            );
            await run.StartAsync();
            await Program.WaitUntilAsync(() => run.IsFinished, "Diagnostic deadline");
            Program.Check(
                run.Recorder.Snapshot().Phase == "Timed out" && context.Helper.Disposed && context.Helper.Stops == 1,
                "The diagnostic deadline stops and drains the owned helper"
            );
        }
        await using (var context = await TestContext.CreateAsync(root, "trace-disconnect"))
        {
            await context.Service.StartAsync(false, false);
            context.Helper.Disconnect = true;
            await Task.Delay(500);
            Program.Check(
                context.Service.Current!.ProcessTrace.State == "Incomplete" && context.Helper.Starts == 1,
                "Disconnected tracing records a gap without relaunching or stopping basic monitoring"
            );
            await context.Service.StopAsync();
        }
    }
}
