using LockscreenGif.Contracts.Services;
using LockscreenGif.Services.Analytics;
using LockscreenGif.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LockscreenGif.Views;

public sealed partial class ShellPage : Page
{
    private readonly INavigationService _navigationService;
    private bool _synchronizingSelection;
    private bool _analyticsReady;

    public Frame NavigationFrame => ContentFrame;

    public ShellPage()
    {
        InitializeComponent();
        _analytics = App.GetService<AnalyticsService>();
        _navigationService = App.GetService<INavigationService>();
        _navigationService.Frame = ContentFrame;
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_synchronizingSelection || _navigationService is null)
        {
            return;
        }

        var viewModel =
            ReferenceEquals(args.SelectedItem, SettingsItem) ? typeof(SettingsViewModel)
            : ReferenceEquals(args.SelectedItem, DiagnosticsItem) ? typeof(DiagnosticsViewModel)
            : typeof(MainViewModel);
        _navigationService.NavigateTo(viewModel.FullName!, clearNavigation: true);
    }

    private void ContentFrame_Navigated(object sender, NavigationEventArgs args)
    {
        if (args.SourcePageType == typeof(SettingsPage))
        {
            AnalyticsNotice.IsOpen = false;
        }
        _synchronizingSelection = true;
        Navigation.SelectedItem =
            args.SourcePageType == typeof(SettingsPage) ? SettingsItem
            : args.SourcePageType == typeof(DiagnosticsPage) ? DiagnosticsItem
            : LockscreenItem;
        _synchronizingSelection = false;
        if (_analyticsReady)
        {
            TrackCurrentPage();
        }
    }

    public void StartPageAnalytics()
    {
        _analyticsReady = true;
        TrackCurrentPage();
    }

    private void TrackCurrentPage() =>
        _analytics?.Track(
            AnalyticsEvent.PageViewed,
            new()
            {
                Page =
                    ContentFrame.Content is SettingsPage ? AnalyticsPage.Settings
                    : ContentFrame.Content is DiagnosticsPage ? AnalyticsPage.Diagnostics
                    : AnalyticsPage.Lockscreen,
            }
        );
}
