using LockscreenGif.Services.Analytics;
using LockscreenGif.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LockscreenGif.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; } = App.GetService<SettingsViewModel>();
    private AnalyticsService? _analytics;
    private bool _updatingToggle;

    public SettingsPage()
    {
        InitializeComponent();
        _analytics = App.GetService<AnalyticsService>();
        RefreshPreference();
        InitializeWindowsImageFeatureSettings();
    }

    private void RefreshPreference()
    {
        _updatingToggle = true;
        AnalyticsToggle.IsOn = _analytics?.IsEnabled == true;
        AnalyticsToggle.IsEnabled = _analytics?.IsConfigured == true;
        _updatingToggle = false;
        if (_analytics?.IsConfigured != true)
        {
            AnalyticsStatus.Title = "Analytics is not configured in this build";
            AnalyticsStatus.IsOpen = true;
        }
    }

    private void AnalyticsToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (_updatingToggle || _analytics is null)
        {
            return;
        }

        AnalyticsSettingsError.IsOpen = !_analytics.SetEnabled(AnalyticsToggle.IsOn);
        RefreshPreference();
    }
}
