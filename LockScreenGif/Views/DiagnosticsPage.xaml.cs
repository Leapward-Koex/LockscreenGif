using LockscreenGif.Contracts.Services;
using LockscreenGif.ViewModels;
using LockscreenGif.Views.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace LockscreenGif.Views;

public sealed partial class DiagnosticsPage : Page
{
    public DiagnosticsViewModel ViewModel { get; }
    public string AppVersion => LockscreenGif.Helpers.BuildInfo.DisplayVersion;
    private bool _exportPickerOpen;
    private DiagnosticLockDialog? _lockCountdown;

    public DiagnosticsPage()
    {
        ViewModel = App.GetService<DiagnosticsViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            _lockCountdown?.Cancel();
            ViewModel.Detach();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        ViewModel.Attach(DispatcherQueue);
        if (ViewModel.CanConfigure)
        {
            await ViewModel.RefreshReadinessAsync();
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs args) => await ViewModel.RefreshReadinessAsync();

    private async void Start_Click(object sender, RoutedEventArgs args)
    {
        if (!ViewModel.CanStart || _lockCountdown is not null)
        {
            return;
        }

        await ViewModel.StartAsync();
        if (!IsLoaded || !ViewModel.CanLock)
        {
            return;
        }

        var countdown = new DiagnosticLockDialog(XamlRoot, DispatcherQueue);
        _lockCountdown = countdown;
        void OnStateChanged(object? source, System.ComponentModel.PropertyChangedEventArgs change)
        {
            // Dismiss if Windows was locked manually, the test stopped, or the app is closing.
            if (!ViewModel.CanLock)
            {
                countdown.Cancel();
            }
        }
        ViewModel.PropertyChanged += OnStateChanged;
        try
        {
            if (await countdown.ShowAsync() && IsLoaded && ViewModel.CanLock)
            {
                ViewModel.LockNow();
            }
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(ex);
        }
        finally
        {
            ViewModel.PropertyChanged -= OnStateChanged;
            _lockCountdown = null;
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs args) => await ViewModel.StopAsync();

    private void Lock_Click(object sender, RoutedEventArgs args) => ViewModel.LockNow();

    private void ChooseSource_Click(object sender, RoutedEventArgs args) =>
        App.GetService<INavigationService>().NavigateTo(typeof(MainViewModel).FullName!, clearNavigation: true);

    private async void Export_Click(object sender, RoutedEventArgs args)
    {
        if (_exportPickerOpen || !ViewModel.CanExport)
        {
            return;
        }

        _exportPickerOpen = true;
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"LockscreenGif-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}",
            };
            picker.FileTypeChoices.Add("Diagnostics report", new List<string> { ".zip" });
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            var destination = await picker.PickSaveFileAsync();
            if (destination is not null)
            {
                await ViewModel.ExportAsync(destination.Path);
            }
        }
        catch (Exception ex)
        {
            ViewModel.ShowError(ex);
        }
        finally
        {
            _exportPickerOpen = false;
        }
    }
}
