using LockscreenGif.Contracts.Services;
using LockscreenGif.Services.Analytics;
using LockscreenGif.Services.Diagnostics;
using LockscreenGif.Services.Lockscreen;
using LockscreenGif.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace LockscreenGif;

public partial class App
{
    private bool _closePending;

    private void MainWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        if (_closePending)
        {
            return;
        }

        _closePending = true;
        // Always leave the native Closing callback before removing message hooks
        // or calling Close, even when every shutdown task completes synchronously.
        if (!MainWindow.DispatcherQueue.TryEnqueue(async () => await CloseWindowAsync()))
        {
            // A dispatcher that is already shutting down cannot run deferred work.
            StopAnalytics();
            args.Cancel = false;
        }
    }

    private async Task CloseWindowAsync()
    {
        var diagnostics = GetService<DiagnosticsSessionService>();
        var lockscreen = GetService<ILockscreenService>();
        var verification = GetService<LockscreenVerificationService>();
        (MainWindow.Content as ShellPage)?.StopLockscreenFeedback();
        if (MainWindow.Content is Microsoft.UI.Xaml.Controls.Control control)
        {
            control.IsEnabled = false;
        }
        else if (MainWindow.Content is UIElement content)
        {
            content.IsHitTestVisible = false;
        }

        try
        {
            await verification.CloseAsync();
            if (diagnostics.IsRunning)
            {
                await diagnostics.CloseAsync("The app closed before the diagnostic test completed.");
            }
        }
        catch (Exception ex)
        {
            try
            {
                GetService<IErrorReporter>().CaptureException(ex, AnalyticsErrorContext.AppShutdown, AnalyticsWorkflow.Diagnostics);
            }
            catch { }
            Logger.Error("Could not finish the diagnostic session while closing", ex);
            diagnostics.Interrupt("The app closed while diagnostic shutdown encountered an error.");
        }
        finally
        {
            // Ordinary Lockscreen-page applies also finish their native operations first.
            await lockscreen.WaitForIdleAsync();
            await GetService<WindowsImageFeatureService>().WaitForIdleAsync();
            StopAnalytics();
            GetService<WindowsSessionMonitor>().Dispose();
            MainWindow.AppWindow.Closing -= MainWindow_Closing;
            MainWindow.Close();
        }
    }
}
