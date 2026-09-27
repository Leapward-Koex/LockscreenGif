using LockscreenGif;
using LockscreenGif.Contracts.Services;
using LockscreenGif.Services.Diagnostics;
using LockscreenGif.Views;

namespace WindowLifecycle.Tests;

internal static class Program
{
    private static int _checks;

    private static async Task Main()
    {
        PlaybackLifetime();
        await IdleCloseAsync();
        await PendingCloseAsync();
        await FailedVerificationAsync();
        DispatcherShutdown();
        Console.WriteLine($"All {_checks} window lifecycle checks passed. No native APIs or analytics transports were used.");
    }

    private static void PlaybackLifetime()
    {
        foreach (var unloadFirst in new[] { false, true })
        {
            _ = new App();
            var player = new FakePlayer();
            var page = new MainPage(player);
            player.BeforeDispose = () =>
            {
                Check(
                    page.VideoPreview.MediaPlayer is null && !page.HasSession && !page.MediaReady,
                    "detach player and session before disposal"
                );
                Check(player.Subscribers == 0 && player.Session.Subscribers == 0, "unsubscribe media and seek callbacks before disposal");
            };
            if (unloadFirst)
            {
                page.Unload();
                Check(player.DisposeCalls == 0 && page.MediaReady, "navigation unload preserves the reusable video draft");
            }
            App.MainWindow.Close();
            page.Unload();
            page.Unload();
            page.Release();
            Check(player.DisposeCalls == 1, "repeated cleanup disposes the player exactly once");
            Check(player.PauseCalls == (unloadFirst ? 1 : 0), "unload never pauses a disposed player");
            Check(page.Suspended && page.WorkCancelled && !page.Polling, "close stops rendering, requests, polling, and interaction");
        }

        _ = new App();
        var empty = new MainPage(null);
        App.MainWindow.Close();
        empty.Unload();
        Check(empty.Suspended && empty.WorkCancelled, "closing with no video is safe");

        _ = new App();
        var replaced = new FakePlayer();
        var draft = new MainPage(replaced);
        draft.Unload();
        draft.Release();
        App.MainWindow.Close();
        draft.Unload();
        Check(replaced.DisposeCalls == 1 && !draft.HasSession, "releasing a draft before window close remains safe");
    }

    private static (
        DiagnosticsSessionService Diagnostics,
        LockscreenVerificationService Verification,
        FakeLockscreenService Lockscreen,
        WindowsSessionMonitor Monitor
    ) Setup()
    {
        _ = new App();
        var diagnostics = new DiagnosticsSessionService();
        var verification = new LockscreenVerificationService();
        var lockscreen = new FakeLockscreenService();
        var monitor = new WindowsSessionMonitor();
        App.Register(diagnostics);
        App.Register(verification);
        App.Register<ILockscreenService>(lockscreen);
        App.Register(monitor);
        return (diagnostics, verification, lockscreen, monitor);
    }

    private static async Task IdleCloseAsync()
    {
        var services = Setup();
        var window = App.MainWindow;
        var shell = (ShellPage)window.Content!;
        var player = new FakePlayer();
        var page = new MainPage(player);
        Check(window.AppWindow.RequestClose(), "defer the initial native close request");
        Check(window.CloseCalls == 0 && services.Monitor.DisposeCalls == 0, "do not close or remove hooks inside Closing");
        Check(window.DispatcherQueue.Count == 1, "one click schedules shutdown");
        window.DispatcherQueue.Pump();
        await window.CloseCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        page.Unload();
        Check(window.CloseCalls == 1 && services.Monitor.DisposeCalls == 1, "idle shutdown finishes without a second click");
        Check(shell.FeedbackStops == 1 && !shell.IsEnabled && App.AnalyticsStops == 1, "stop feedback and analytics once");
        Check(player.DisposeCalls == 1 && player.PauseCalls == 0, "one-click shutdown safely unloads video preview");
        Check(
            !window.AppWindow.RequestClose() && window.DispatcherQueue.Count == 0,
            "closing handler is detached before programmatic close"
        );
    }

    private static async Task PendingCloseAsync()
    {
        var services = Setup();
        var verification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnostic = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var apply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        services.Verification.Completion = verification.Task;
        services.Diagnostics.IsRunning = true;
        services.Diagnostics.Completion = diagnostic.Task;
        services.Lockscreen.Idle = apply.Task;
        var window = App.MainWindow;
        window.AppWindow.RequestClose();
        window.AppWindow.RequestClose();
        Check(window.DispatcherQueue.Count == 1, "repeated clicks cannot schedule concurrent shutdowns");
        window.DispatcherQueue.Pump();
        Check(window.CloseCalls == 0 && services.Verification.CloseCalls == 1, "wait for verification cleanup");
        verification.SetResult();
        await UntilAsync(() => services.Diagnostics.CloseCalls == 1);
        Check(window.CloseCalls == 0, "wait for active diagnostic cleanup");
        diagnostic.SetResult();
        Check(window.CloseCalls == 0 && services.Monitor.DisposeCalls == 0, "keep window and monitoring until apply is idle");
        Check(window.AppWindow.RequestClose() && window.DispatcherQueue.Count == 0, "clicks during cleanup remain deferred");
        apply.SetResult();
        await window.CloseCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(window.CloseCalls == 1 && services.Monitor.DisposeCalls == 1, "pending shutdown automatically closes once after cleanup");
    }

    private static async Task FailedVerificationAsync()
    {
        var services = Setup();
        services.Verification.Completion = Task.FromException(new InvalidOperationException("Synthetic shutdown failure"));
        App.MainWindow.AppWindow.RequestClose();
        App.MainWindow.DispatcherQueue.Pump();
        await App.MainWindow.CloseCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(services.Diagnostics.Interrupted && App.MainWindow.CloseCalls == 1, "verification failure still finalizes shutdown");
    }

    private static void DispatcherShutdown()
    {
        Setup();
        App.MainWindow.DispatcherQueue.AcceptWork = false;
        Check(!App.MainWindow.AppWindow.RequestClose(), "do not strand a close request when the dispatcher has stopped");
        Check(App.AnalyticsStops == 1, "dispatcher shutdown stops analytics without waiting");
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
        _checks++;
    }
}
