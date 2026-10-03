using System.Globalization;
using LockscreenGif.Contracts.Services;
using LockscreenGif.Privileged;
using LockscreenGif.Services.Diagnostics;
using LockscreenGif.Services.Lockscreen;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LockscreenGif.Views;

public sealed partial class SettingsPage
{
    private bool _busy;
    private bool _loaded;
    private bool _refreshing;
    private ContentDialog? _featureIdDialog;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _refreshTimer;

    private void InitializeWindowsImageFeatureSettings()
    {
        Loaded += (_, _) =>
        {
            _loaded = true;
            FeatureIdInput.Text = WindowsImageFeatureSettings.Current.FeatureId.ToString(CultureInfo.InvariantCulture);
            DefaultFeatureIdText.Text = $"Default ID: {WindowsImageFeature.DefaultFeatureId}.";
            UpdatePreferenceControls();
            _refreshTimer ??= DispatcherQueue.CreateTimer();
            _refreshTimer.Interval = TimeSpan.FromSeconds(5);
            _refreshTimer.Tick -= RefreshTimer_Tick;
            _refreshTimer.Tick += RefreshTimer_Tick;
            App.MainWindow.Activated -= Window_Activated;
            App.MainWindow.Activated += Window_Activated;
            _refreshTimer.Start();
            _ = RefreshFeatureAsync();
        };
        Unloaded += (_, _) =>
        {
            _loaded = false;
            _featureIdDialog?.Hide();
            _refreshTimer?.Stop();
            App.MainWindow.Activated -= Window_Activated;
        };
    }

    private void RefreshTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) => _ = RefreshFeatureAsync();

    private void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            _ = RefreshFeatureAsync();
        }
    }

    private async Task RefreshFeatureAsync()
    {
        if (!_loaded || _refreshing)
        {
            return;
        }
        _refreshing = true;
        try
        {
            WindowsImageFeatureState state;
            do
            {
                state = await Task.Run(WindowsImageFeatureSettings.Current.Read);
            } while (_loaded && state.FeatureId != WindowsImageFeatureSettings.Current.FeatureId);
            if (!_loaded)
            {
                return;
            }
            var known = state.QueryStatus == 0 && state.QueryError is null;
            FeatureStateText.Text =
                known && state.RuntimeState == 2 ? $"Feature {state.FeatureId}: Enabled."
                : known && state.RuntimeState == 1 ? $"Feature {state.FeatureId}: Disabled."
                : $"The state of feature {state.FeatureId} could not be read. Check the ID or run a diagnostic test for details.";
            EnableWindowsImageFeatureButton.IsEnabled =
                !IsOperationBusy && WindowsImageFeature.Evaluate(state, enabled: true).Outcome == "NeedsChange";
            UpdatePreferenceControls();
        }
        catch (Exception ex)
        {
            Logger.Error("Windows feature settings inspection failed", ex);
            if (_loaded)
            {
                FeatureStateText.Text = "The current Windows feature state could not be read. Run a diagnostic test for details.";
                EnableWindowsImageFeatureButton.IsEnabled = false;
                UpdatePreferenceControls();
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private bool IsOperationBusy =>
        _busy
        || App.GetService<WindowsImageFeatureService>().IsBusy
        || App.GetService<ILockscreenService>().IsApplying
        || App.GetService<LockscreenVerificationService>().IsRunning
        || App.GetService<DiagnosticsSessionService>().IsRunning;

    private void UpdatePreferenceControls()
    {
        var available = !IsOperationBusy;
        FeatureIdInput.IsEnabled = available;
        SaveFeatureIdButton.IsEnabled = available;
        ResetFeatureIdButton.IsEnabled = available;
        WindowsApiToggle.IsEnabled = available;
        RemoveLockscreenButton.IsEnabled = available;
    }

    private async void SaveFeatureId_Click(object sender, RoutedEventArgs args) => await SaveFeatureIdAsync(reset: false);

    private async void ResetFeatureId_Click(object sender, RoutedEventArgs args) => await SaveFeatureIdAsync(reset: true);

    private async Task SaveFeatureIdAsync(bool reset)
    {
        var action = reset ? "ResetWindowsImageFeatureId" : "ChangeWindowsImageFeatureId";
        if (IsOperationBusy)
        {
            DiagnosticsActionLog.Record(action, "Blocked", "Another apply, Windows feature action, or diagnostic test is running.");
            ShowFeatureIdStatus(
                "Wait for the current operation",
                "Finish applying or stop the diagnostic test before changing the feature ID.",
                InfoBarSeverity.Warning
            );
            return;
        }
        var featureId = WindowsImageFeature.DefaultFeatureId;
        if (!reset && !WindowsImageFeatureSettings.TryParseFeatureId(FeatureIdInput.Text, out featureId))
        {
            DiagnosticsActionLog.Record(action, "InvalidInput", "The feature ID must be a positive 32-bit decimal number.");
            ShowFeatureIdStatus("Enter a valid feature ID", "Use a whole number from 1 to 4294967295.", InfoBarSeverity.Warning);
            return;
        }
        var settings = WindowsImageFeatureSettings.Current;
        var previous = settings.FeatureId;
        _busy = true;
        EnableWindowsImageFeatureButton.IsEnabled = false;
        UpdatePreferenceControls();
        DiagnosticsActionLog.Record(action, "Started", $"PreviousFeatureId={previous}; FeatureId={featureId}.");
        try
        {
            if (!reset && !await ConfirmFeatureIdChangeAsync(featureId))
            {
                DiagnosticsActionLog.Record(
                    action,
                    "Cancelled",
                    $"PreviousFeatureId={previous}; RequestedFeatureId={featureId}; WindowsFeaturesChanged=False."
                );
                return;
            }
            if (reset)
            {
                settings.Reset();
            }
            else
            {
                settings.Save(featureId);
            }
            FeatureIdInput.Text = settings.FeatureId.ToString(CultureInfo.InvariantCulture);
            FeatureActionStatus.IsOpen = false;
            DiagnosticsActionLog.Record(
                action,
                "Succeeded",
                $"PreviousFeatureId={previous}; FeatureId={settings.FeatureId}; WindowsFeaturesChanged=False."
            );
            ShowFeatureIdStatus(
                reset ? "Default feature ID restored" : "Feature ID saved",
                $"The app now uses feature {settings.FeatureId}. No Windows feature state was changed.",
                InfoBarSeverity.Success
            );
        }
        catch (Exception ex)
        {
            DiagnosticsActionLog.Record(
                action,
                "Failed",
                $"PreviousFeatureId={previous}; RequestedFeatureId={featureId}; {ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}"
            );
            ShowFeatureIdStatus(
                "The feature ID could not be saved",
                $"The app still uses feature {settings.FeatureId}. {ex.Message}",
                InfoBarSeverity.Warning
            );
        }
        finally
        {
            _busy = false;
            UpdatePreferenceControls();
            await RefreshFeatureAsync();
        }
    }

    private async Task<bool> ConfirmFeatureIdChangeAsync(uint featureId)
    {
        if (!_loaded)
        {
            return false;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = (XamlRoot.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default,
            Title = "Change the Windows feature ID?",
            Content = new TextBlock
            {
                Text =
                    $"Only change this ID if you're sure you know what you're doing. The app will check and control Windows feature {featureId}. Using the wrong ID could affect an unrelated Windows feature.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Save ID",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        _featureIdDialog = dialog;
        try
        {
            var choice = await dialog.ShowAsync();
            return _loaded && choice == ContentDialogResult.Primary;
        }
        finally
        {
            _featureIdDialog = null;
        }
    }

    private void ShowFeatureIdStatus(string title, string message, InfoBarSeverity severity)
    {
        FeatureIdStatus.Title = title;
        FeatureIdStatus.Message = message;
        FeatureIdStatus.Severity = severity;
        FeatureIdStatus.IsOpen = true;
    }

    private async void EnableWindowsImageFeature_Click(object sender, RoutedEventArgs args)
    {
        const string action = "EnableWindowsImageFeature";
        var service = App.GetService<WindowsImageFeatureService>();
        if (IsOperationBusy)
        {
            DiagnosticsActionLog.Record(action, "Blocked", "Another apply, Windows feature action, or diagnostic test is running.");
            FeatureActionStatus.Title = "Wait for the current operation";
            FeatureActionStatus.Message = "Finish applying or stop the diagnostic test before changing the Windows feature.";
            FeatureActionStatus.Severity = InfoBarSeverity.Warning;
            FeatureActionStatus.IsOpen = true;
            return;
        }
        _busy = true;
        EnableWindowsImageFeatureButton.IsEnabled = false;
        UpdatePreferenceControls();
        FeatureActionStatus.IsOpen = false;
        DiagnosticsActionLog.Record(action, "Started");
        try
        {
            var result = await service.SetEnabledAsync(true);
            DiagnosticsActionLog.Record(action, result.Outcome, feature: result);
            var success = result.Outcome is "Enabled" or "AlreadyEnabled";
            FeatureActionStatus.Title = success
                ? $"Windows feature {result.FeatureId} enabled"
                : $"Windows feature {result.FeatureId} was not enabled";
            FeatureActionStatus.Message = success
                ? result.FeatureId == WindowsImageFeature.DefaultFeatureId
                    ? "Animated GIF lockscreens will no longer work on Windows 11 25H2 or later. Disable the feature in Prerequisites to use them again."
                    : "The selected feature is enabled. A custom feature ID requires separate verification of lock-screen behavior."
                : result.Error ?? "The setting could not be changed. Run a diagnostic test for details.";
            if (success)
            {
                FeatureActionStatus.Message += " Restart Windows if the lock screen does not use the changed setting.";
            }
            if (result.ChangeOutcomeUnknown)
            {
                FeatureActionStatus.Message += " The final setting could not be confirmed. Check its status before trying again.";
            }
            FeatureActionStatus.Severity = InfoBarSeverity.Warning;
            FeatureActionStatus.IsOpen = true;
        }
        catch (Exception ex)
        {
            DiagnosticsActionLog.Record(action, "Failed", $"{ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}");
            FeatureActionStatus.Title = "Windows feature was not enabled";
            FeatureActionStatus.Message = ex.Message;
            FeatureActionStatus.Severity = InfoBarSeverity.Warning;
            FeatureActionStatus.IsOpen = true;
        }
        finally
        {
            _busy = false;
            UpdatePreferenceControls();
            await RefreshFeatureAsync();
        }
    }
}
