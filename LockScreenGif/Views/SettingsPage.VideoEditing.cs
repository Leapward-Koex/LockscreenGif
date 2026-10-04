using System.ComponentModel;
using LockscreenGif.Services;
using Microsoft.UI.Xaml;

namespace LockscreenGif.Views;

public sealed partial class SettingsPage
{
    private HardwareDecodingCapabilityService? _hardwareDecodingCapability;
    private bool _hardwareSettingsLoaded;
    private bool _updatingHardwareDecodingToggle;

    private void InitializeHardwareDecodingSettings()
    {
        _hardwareDecodingCapability = App.GetService<HardwareDecodingCapabilityService>();
        Loaded += HardwareDecodingSettings_Loaded;
        Unloaded += HardwareDecodingSettings_Unloaded;
        RefreshHardwareDecodingSettings();
    }

    private async void HardwareDecodingSettings_Loaded(object sender, RoutedEventArgs args)
    {
        if (_hardwareSettingsLoaded || _hardwareDecodingCapability is null)
        {
            return;
        }
        _hardwareSettingsLoaded = true;
        _hardwareDecodingCapability.Changed += HardwareDecodingCapability_Changed;
        ViewModel.VideoEditingPreferences.PropertyChanged += VideoEditingPreferences_PropertyChanged;
        RefreshHardwareDecodingSettings();
        try
        {
            await _hardwareDecodingCapability.EnsureCheckedAsync(retryUnsuccessful: true);
        }
        catch (Exception ex)
        {
            Logger.Error("Hardware decoding availability check failed", ex);
        }
    }

    private void HardwareDecodingSettings_Unloaded(object sender, RoutedEventArgs args)
    {
        _hardwareSettingsLoaded = false;
        if (_hardwareDecodingCapability is not null)
        {
            _hardwareDecodingCapability.Changed -= HardwareDecodingCapability_Changed;
        }
        ViewModel.VideoEditingPreferences.PropertyChanged -= VideoEditingPreferences_PropertyChanged;
    }

    private void HardwareDecodingCapability_Changed(object? sender, EventArgs args) => QueueHardwareDecodingSettingsRefresh();

    private void VideoEditingPreferences_PropertyChanged(object? sender, PropertyChangedEventArgs args) =>
        QueueHardwareDecodingSettingsRefresh();

    private void QueueHardwareDecodingSettingsRefresh()
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            RefreshIfLoaded();
        }
        else
        {
            DispatcherQueue.TryEnqueue(RefreshIfLoaded);
        }

        void RefreshIfLoaded()
        {
            if (_hardwareSettingsLoaded)
            {
                RefreshHardwareDecodingSettings();
            }
        }
    }

    private void RefreshHardwareDecodingSettings()
    {
        var state = VideoEditingSettingsState.Create(
            _hardwareDecodingCapability?.Snapshot.Availability ?? HardwareDecodingAvailability.Checking,
            ViewModel.VideoEditingPreferences.UseHardwareDecoding
        );
        _updatingHardwareDecodingToggle = true;
        try
        {
            HardwareDecodingToggle.IsEnabled = state.IsEnabled;
            HardwareDecodingToggle.IsOn = state.IsOn;
            HardwareDecodingAvailabilityText.Text = state.Description;
        }
        finally
        {
            _updatingHardwareDecodingToggle = false;
        }
    }

    private void HardwareDecodingToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (_updatingHardwareDecodingToggle || _hardwareDecodingCapability?.Snapshot.Availability != HardwareDecodingAvailability.Available)
        {
            return;
        }
        ViewModel.VideoEditingPreferences.UseHardwareDecoding = HardwareDecodingToggle.IsOn;
        RefreshHardwareDecodingSettings();
    }
}
