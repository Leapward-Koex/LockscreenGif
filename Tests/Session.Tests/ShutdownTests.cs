namespace Session.Tests;

internal static class ShutdownTests
{
    public static async Task RunAsync(string root)
    {
        await CloseWaitsForNativeBoundaryAsync(root);
        await CloseDuringMonitoringAsync(root);
        await CancelBeforeMonitorIterationAsync(root);
        await MissingPowerRegistrationAsync(root);
    }

    private static async Task CloseWaitsForNativeBoundaryAsync(string root)
    {
        await using var context = await TestContext.CreateAsync(root, "close-native-boundary");
        context.Lockscreen.DeferCancellation = true;
        var starting = context.Service.StartAsync(false, true);
        await context.Lockscreen.ApplyEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var closing = context.Service.CloseAsync("The app closed during the test.");
        await Task.Delay(100);
        Program.Check(
            !closing.IsCompleted && context.Service.IsRunning && context.Lockscreen.IsApplying,
            "closing waits for an in-flight native apply to reach its safe boundary"
        );
        context.Lockscreen.ReleaseApply.TrySetResult();
        await Task.WhenAll(starting, closing).WaitAsync(TimeSpan.FromSeconds(10));
        Program.Check(
            !context.Service.IsRunning && !context.Lockscreen.IsApplying,
            "closing drains preparation and native apply before returning"
        );
        Program.Check(
            context.Service.Current!.Phase == "Interrupted"
                && context.Service.Current.Error == "The app closed during the test."
                && context.Service.Current?.EndedAt is not null,
            "deferred closing preserves the Interrupted result and shutdown reason exactly once"
        );
    }

    private static async Task CloseDuringMonitoringAsync(string root)
    {
        await using var context = await TestContext.CreateAsync(root, "close-monitoring");
        await context.Service.StartAsync(false, false);
        await context.Service.CloseAsync("Closing while waiting for lock.").WaitAsync(TimeSpan.FromSeconds(3));
        Program.Check(
            !context.Service.IsRunning && context.Service.Current!.Phase == "Interrupted",
            "closing an idle monitor completes promptly without waiting for the session timeout"
        );
    }

    private static async Task CancelBeforeMonitorIterationAsync(string root)
    {
        await using var context = await TestContext.CreateAsync(root, "cancel-before-monitor");
        Task? stopping = null;
        var requested = false;
        context.Service.Changed += (_, _) =>
        {
            if (requested || context.Service.Current?.Phase != "Waiting for lock")
            {
                return;
            }

            requested = true;
            stopping = context.Service.StopAsync();
        };
        await context.Service.StartAsync(false, false).WaitAsync(TimeSpan.FromSeconds(10));
        if (stopping is not null)
        {
            await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Program.Check(
            requested && !context.Service.IsRunning && context.Service.Current!.Phase == "Cancelled",
            "cancellation before the monitor's first iteration still finalizes the session"
        );
        Program.Check(context.Service.Current?.EndedAt is not null, "boundary cancellation finalizes the current result");
    }

    private static async Task MissingPowerRegistrationAsync(string root)
    {
        await using var context = await TestContext.CreateAsync(root, "missing-power-monitor");
        context.Windows.PowerNotificationsAvailable = false;
        context.Windows.PowerError = "Synthetic power registration failure";
        await context.Service.StartAsync(false, false);
        await context.Service.StopAsync();
        Program.Check(
            !context.Service.Current!.MonitoringComplete
                && context.Service.Current.Events.Any(e => e.Message.Contains("Synthetic power registration failure")),
            "power registration failure is retained as a collection gap"
        );
    }
}
