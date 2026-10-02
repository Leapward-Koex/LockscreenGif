using System.Diagnostics;
using LockscreenGif.Privileged;
using LockscreenGif.Services.Diagnostics;
using LockscreenGif.Services.Lockscreen;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace LockscreenGif.Views;

public sealed partial class MainPage
{
    private bool _prerequisitesLoaded;
    private bool _refreshingPrerequisites;
    private LockscreenPrerequisiteStatus? _prerequisites;
    private ContentDialog? _featureRebootDialog;

    private void StartLockscreenModePolling()
    {
        _prerequisitesLoaded = true;
        _lockscreenModeTimer ??= DispatcherQueue.CreateTimer();
        _lockscreenModeTimer.Interval = TimeSpan.FromSeconds(5);
        _lockscreenModeTimer.IsRepeating = true;
        _lockscreenModeTimer.Tick -= PrerequisitesTimer_Tick;
        _lockscreenModeTimer.Tick += PrerequisitesTimer_Tick;
        App.MainWindow.Activated -= PrerequisitesWindow_Activated;
        App.MainWindow.Activated += PrerequisitesWindow_Activated;
        _lockscreenModeTimer.Start();
        _ = RefreshPrerequisitesAsync();
    }

    private void StopLockscreenModePolling()
    {
        _prerequisitesLoaded = false;
        _featureRebootDialog?.Hide();
        _lockscreenModeTimer?.Stop();
        App.MainWindow.Activated -= PrerequisitesWindow_Activated;
    }

    private void PrerequisitesTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) =>
        _ = RefreshPrerequisitesAsync();

    private void PrerequisitesWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            _ = RefreshPrerequisitesAsync();
        }
    }

    private async Task RefreshPrerequisitesAsync()
    {
        if (_refreshingPrerequisites || !_prerequisitesLoaded)
        {
            return;
        }
        _refreshingPrerequisites = true;
        try
        {
            var state = await Task.Run(() => LockscreenPrerequisites.Read(_lockscreenService.CacheDirectory));
            if (!_prerequisitesLoaded)
            {
                return;
            }
            _prerequisites = state;
            PicturePrerequisiteDetail.Text = state.PictureAndCache.Detail;
            FeaturePrerequisiteDetail.Text = state.ImageFeature.Detail;
            SetPrerequisiteIcon(PicturePrerequisiteIcon, state.PictureAndCache.Satisfied);
            SetPrerequisiteIcon(FeaturePrerequisiteIcon, state.ImageFeature.Satisfied);
            PrerequisitesPanel.Style = (Style)Resources[state.Satisfied ? "PrerequisitesReadyStyle" : "PrerequisitesWarningStyle"];
            UpdatePrerequisiteButtons();
        }
        catch (Exception ex)
        {
            Logger.Error("Prerequisite inspection failed", ex);
            _prerequisites = null;
            if (!_prerequisitesLoaded)
            {
                return;
            }
            PicturePrerequisiteDetail.Text =
                "Prerequisites could not be checked. Open Windows lock-screen settings and run a diagnostic test.";
            FeaturePrerequisiteDetail.Text = "The Windows feature status could not be checked. Run a diagnostic test for details.";
            SetPrerequisiteIcon(PicturePrerequisiteIcon, false);
            SetPrerequisiteIcon(FeaturePrerequisiteIcon, false);
            PrerequisitesPanel.Style = (Style)Resources["PrerequisitesWarningStyle"];
            DisableWindowsImageFeatureButton.IsEnabled = false;
        }
        finally
        {
            _refreshingPrerequisites = false;
            if (_prerequisitesLoaded)
            {
                RefreshFlowUi();
            }
        }
    }

    private void SetPrerequisiteIcon(FontIcon icon, bool satisfied)
    {
        icon.Glyph = satisfied ? "\uE73E" : "\uE7BA";
        icon.Style = (Style)Resources[satisfied ? "PrerequisiteCompleteIconStyle" : "PrerequisiteWarningIconStyle"];
        AutomationProperties.SetName(icon, satisfied ? "Prerequisite satisfied" : "Prerequisite needs attention");
    }

    private void UpdatePrerequisiteButtons()
    {
        var busy =
            _actionPending
            || _lockscreenService.IsApplying
            || App.GetService<WindowsImageFeatureService>().IsBusy
            || App.GetService<DiagnosticsSessionService>().IsRunning
            || App.GetService<LockscreenVerificationService>().IsRunning;
        OpenLockscreenSettingsButton.IsEnabled = !busy;
        DisableWindowsImageFeatureButton.IsEnabled = !busy && _prerequisites?.ImageFeature.CanAct == true;
    }

    private async Task RunPrerequisiteActionAsync(string action, Func<Task> perform)
    {
        if (
            _actionPending
            || _lockscreenService.IsApplying
            || App.GetService<WindowsImageFeatureService>().IsBusy
            || App.GetService<DiagnosticsSessionService>().IsRunning
            || App.GetService<LockscreenVerificationService>().IsRunning
        )
        {
            DiagnosticsActionLog.Record(
                action,
                "Blocked",
                "Another apply, Windows feature action, diagnostic test, or lock-screen check is running."
            );
            OperationStatus.Title = "Wait for the current operation";
            OperationStatus.Message =
                "Finish the apply, Windows feature action, diagnostic test, or lock-screen check before changing settings.";
            OperationStatus.IsOpen = true;
            return;
        }
        await RunActionAsync(async () =>
        {
            DiagnosticsActionLog.Record(action, "Started");
            try
            {
                await perform();
            }
            catch (Exception ex)
            {
                DiagnosticsActionLog.Record(action, "Failed", $"{ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}");
                throw;
            }
        });
        await RefreshPrerequisitesAsync();
    }

    private async void OpenLockscreenSettings_Click(object sender, RoutedEventArgs args) =>
        await RunPrerequisiteActionAsync(
            "OpenLockscreenSettings",
            async () =>
            {
                var opened = await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:lockscreen"));
                DiagnosticsActionLog.Record(
                    "OpenLockscreenSettings",
                    opened ? "Succeeded" : "Failed",
                    "Requested Windows lock-screen settings."
                );
                if (!opened)
                {
                    OperationStatus.Title = "Windows Settings could not be opened";
                    OperationStatus.Message = "Open Settings > Personalization > Lock screen, then choose Picture.";
                    OperationStatus.IsOpen = true;
                }
            }
        );

    private async void DisableWindowsImageFeature_Click(object sender, RoutedEventArgs args) =>
        await RunPrerequisiteActionAsync(
            "DisableWindowsImageFeature",
            async () =>
            {
                if (!LockscreenPrerequisites.Is25H2OrLater(Environment.OSVersion.Version))
                {
                    DiagnosticsActionLog.Record("DisableWindowsImageFeature", "NotRequired", "Windows is earlier than 25H2.");
                    return;
                }
                var result = await App.GetService<WindowsImageFeatureService>().SetEnabledAsync(false);
                DiagnosticsActionLog.Record("DisableWindowsImageFeature", result.Outcome, feature: result);
                ShowFeatureActionResult(result);
                if (result.Outcome is "Disabled" or "AlreadyDisabled" && !result.ChangeOutcomeUnknown)
                {
                    await OfferFeatureRebootAsync();
                }
            }
        );

    private async Task OfferFeatureRebootAsync()
    {
        if (!_prerequisitesLoaded)
        {
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = (XamlRoot.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default,
            Title = "Reboot to see the change?",
            Content = new TextBlock
            {
                Text =
                    "The Windows feature is disabled. You might have to reboot to see the change. Save your work before rebooting, then apply your GIF again after Windows starts.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Reboot",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Close,
        };
        _featureRebootDialog = dialog;
        ContentDialogResult choice;
        try
        {
            choice = await dialog.ShowAsync();
        }
        finally
        {
            _featureRebootDialog = null;
        }
        if (choice != ContentDialogResult.Primary || !_prerequisitesLoaded)
        {
            return;
        }

        DiagnosticsActionLog.Record("RestartWindows", "Requested");
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // A nonzero timeout implies forced app closure; keep the restart graceful.
            start.ArgumentList.Add("/r");
            start.ArgumentList.Add("/t");
            start.ArgumentList.Add("0");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The Windows restart command could not start.");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Windows declined the restart request (exit code {process.ExitCode}).");
            }
            DiagnosticsActionLog.Record("RestartWindows", "RequestAccepted");
        }
        catch (Exception ex)
        {
            var unknown = ex is TimeoutException;
            DiagnosticsActionLog.Record(
                "RestartWindows",
                unknown ? "Unobserved" : "Failed",
                $"{ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}"
            );
            Logger.Error("Windows restart request failed", ex);
            OperationStatus.Title = unknown ? "Restart request not confirmed" : "Windows could not be restarted";
            OperationStatus.Message = unknown
                ? "The restart request could not be confirmed. If Windows does not restart, restart manually, then apply your GIF again."
                : "The feature is disabled. Restart Windows manually to see the change, then apply your GIF again.";
            OperationStatus.Severity = InfoBarSeverity.Warning;
            OperationStatus.IsOpen = true;
        }
    }

    private void ShowFeatureActionResult(WindowsImageFeatureResult result)
    {
        var success = result.Outcome is "Disabled" or "AlreadyDisabled";
        OperationStatus.Title = success
            ? $"Windows feature {result.FeatureId} disabled"
            : $"Windows feature {result.FeatureId} was not configured";
        OperationStatus.Message = success
            ? "The selected Windows feature is disabled. Apply your GIF to check the animation. If the image stays still, restart Windows, then apply your GIF again."
            : result.Error ?? "Run a diagnostic test for more details.";
        if (result.ChangeOutcomeUnknown)
        {
            OperationStatus.Message += " The final setting could not be confirmed. Check its status before trying again.";
        }
        OperationStatus.Severity = success ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        OperationStatus.IsOpen = true;
    }
}
