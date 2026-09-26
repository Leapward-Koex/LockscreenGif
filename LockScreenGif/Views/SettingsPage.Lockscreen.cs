using LockscreenGif.Contracts.Services;
using LockscreenGif.Helpers;
using LockscreenGif.Services.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LockscreenGif.Views;

public sealed partial class SettingsPage
{
    private bool _removalPending;

    private async void RemoveLockscreen_Click(object sender, RoutedEventArgs args)
    {
        if (_removalPending)
        {
            return;
        }

        var lockscreen = App.GetService<ILockscreenService>();
        if (App.GetService<DiagnosticsSessionService>().IsRunning)
        {
            ShowRemovalStatus(
                "A diagnostic test is running",
                "Finish or stop the test on the Diagnostics page before changing the lock screen.",
                InfoBarSeverity.Warning
            );
            return;
        }
        if (App.GetService<LockscreenVerificationService>().IsRunning)
        {
            ShowRemovalStatus(
                "Checking the applied lock screen",
                "Lock and unlock to finish the file-read check before changing the lock screen.",
                InfoBarSeverity.Warning
            );
            return;
        }
        if (lockscreen.IsApplying)
        {
            ShowRemovalStatus(
                "The lock screen is being updated",
                "Wait for the current lockscreen operation to finish, then try again.",
                InfoBarSeverity.Warning
            );
            return;
        }

        _removalPending = true;
        RemoveLockscreenButton.IsEnabled = false;
        RemoveLockscreenButton.Content = "Removing…";
        RemovalStatus.IsOpen = false;
        try
        {
            var notifications = App.GetService<IAppNotificationService>();
            var result = await lockscreen.RemoveAppliedGif();
            if (result is null)
            {
                ShowRemovalStatus(
                    "Could not remove the animated lockscreen",
                    "Check the application logs for details and try again.",
                    InfoBarSeverity.Error
                );
                notifications.Show(string.Format("AppNotificationDeleteFailure".GetLocalized(), AppContext.BaseDirectory));
            }
            else if (result.FailedDeletions != 0)
            {
                ShowRemovalStatus(
                    "Some lock-screen variants could not be removed",
                    $"{result.SuccessfulDeletions} removed; {result.FailedDeletions} could not be removed. Check the application logs for details.",
                    InfoBarSeverity.Warning
                );
                notifications.Show(
                    string.Format(
                        "AppNotificationDeletePartialFailure".GetLocalized(),
                        AppContext.BaseDirectory,
                        result.SuccessfulDeletions,
                        result.FailedDeletions
                    )
                );
            }
            else
            {
                ShowRemovalStatus(
                    "Animated lock-screen variants removed",
                    "You may need to lock and unlock before applying another GIF.",
                    InfoBarSeverity.Success
                );
                notifications.Show(string.Format("AppNotificationDeleteSuccess".GetLocalized(), AppContext.BaseDirectory));
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Settings lockscreen removal failed", ex);
            ShowRemovalStatus("Could not remove the animated lockscreen", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _removalPending = false;
            RemoveLockscreenButton.IsEnabled = true;
            RemoveLockscreenButton.Content = "Remove";
        }
    }

    private void ShowRemovalStatus(string title, string message, InfoBarSeverity severity)
    {
        RemovalStatus.Title = title;
        RemovalStatus.Message = message;
        RemovalStatus.Severity = severity;
        RemovalStatus.IsOpen = true;
    }
}
