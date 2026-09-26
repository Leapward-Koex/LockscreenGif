namespace Session.Tests;

internal static class CancellationTests
{
    public static async Task RunAsync(string root)
    {
        await CancelWhileApplyingAsync(root);
        await InterruptWhileApplyingAsync(root);
        await InterruptWhileMonitoringAsync(root);
    }

    private static async Task CancelWhileApplyingAsync(string root)
    {
        await using var context = await TestContext.CreateAsync(root, "cancel-apply");
        context.Lockscreen.BlockApply = true;
        var start = context.Service.StartAsync(false, false);
        await context.Lockscreen.ApplyEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Program.Check(context.Service.IsRunning && context.Service.Current!.Phase == "Applying", "apply can remain in progress");
        await context.Service.StopAsync();
        await start.WaitAsync(TimeSpan.FromSeconds(10));
        Program.Check(!context.Service.IsRunning && !context.Lockscreen.IsApplying, "stop cancels pending apply and drains startup");
        Program.Check(
            context.Service.Current!.Phase == "Cancelled" && context.Service.Current.EndedAt is not null,
            "cancelled startup has a terminal report"
        );
        Program.Check(
            context.Service.Current?.EndedAt is not null && !Directory.EnumerateFiles(context.Lockscreen.CacheDirectory).Any(),
            "cancelled apply is recorded without writing cache files"
        );
    }

    private static async Task InterruptWhileApplyingAsync(string root)
    {
        await using var context = await TestContext.CreateAsync(root, "interrupt-apply");
        context.Lockscreen.BlockApply = true;
        var start = context.Service.StartAsync(false, false);
        await context.Lockscreen.ApplyEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        context.Service.Interrupt("Simulated app shutdown while applying");
        await start.WaitAsync(TimeSpan.FromSeconds(10));
        Program.Check(context.Service.Current!.Phase == "Interrupted", "apply cancellation cleanup preserves Interrupted");
        Program.Check(
            !context.Service.IsRunning && !context.Service.Current!.MonitoringComplete,
            "interruption stops monitoring and records the gap"
        );
        Program.Check(
            context.Service.Current?.EndedAt is not null && context.Service.Current!.Error!.Contains("shutdown"),
            "interrupted apply keeps its reason in the current result"
        );
    }

    private static async Task InterruptWhileMonitoringAsync(string root)
    {
        await using var context = await TestContext.CreateAsync(root, "interrupt-monitor");
        await context.Service.StartAsync(false, false);
        context.Service.Interrupt("Simulated app shutdown during monitoring");
        await Task.Delay(400); // Allow the cancelled monitor continuation to finish.
        await context.Service.StopAsync();
        Program.Check(
            context.Service.Current!.Phase == "Interrupted" && context.Service.Current?.EndedAt is not null,
            "monitor cancellation cleanup does not overwrite Interrupted"
        );
        var count = context.Service.Current!.Events.Count;
        context.Windows.Emit("SessionLock");
        Program.Check(
            context.Service.Current.Events.Count == count && !context.Service.Current.LockObserved,
            "finished run unsubscribes from native observations"
        );
    }
}
