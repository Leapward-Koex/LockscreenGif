using LockscreenGif.Services.Analytics;
using LockscreenGif.ViewModels;
using Microsoft.UI.Xaml;

namespace LockscreenGif.Views;

public sealed partial class ShellPage
{
    private AnalyticsService? _analytics;

    public void InitializeAnalytics()
    {
        if (_analytics is null || !_analytics.IsConfigured || _analytics.HasSavedPreference)
        {
            return;
        }

        // Establish the first-run default once. Never replace an existing opt-out or unreadable preference.
        if (_analytics.SetEnabled(true))
        {
            AnalyticsNotice.IsOpen = true;
        }
    }

    private void OpenAnalyticsSettings_Click(object sender, RoutedEventArgs args) =>
        _navigationService.NavigateTo(typeof(SettingsViewModel).FullName!, clearNavigation: true);
}
