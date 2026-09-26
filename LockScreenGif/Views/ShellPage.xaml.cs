using LockscreenGif.Contracts.Services;
using LockscreenGif.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LockscreenGif.Views;

public sealed partial class ShellPage : Page
{
    private readonly INavigationService _navigationService;
    private bool _synchronizingSelection;

    public Frame NavigationFrame => ContentFrame;

    public ShellPage()
    {
        InitializeComponent();
        _navigationService = App.GetService<INavigationService>();
        _navigationService.Frame = ContentFrame;
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_synchronizingSelection || _navigationService is null)
        {
            return;
        }

        var viewModel = ReferenceEquals(args.SelectedItem, DiagnosticsItem) ? typeof(DiagnosticsViewModel) : typeof(MainViewModel);
        _navigationService.NavigateTo(viewModel.FullName!, clearNavigation: true);
    }

    private void ContentFrame_Navigated(object sender, NavigationEventArgs args)
    {
        _synchronizingSelection = true;
        Navigation.SelectedItem = args.SourcePageType == typeof(DiagnosticsPage) ? DiagnosticsItem : LockscreenItem;
        _synchronizingSelection = false;
    }
}
