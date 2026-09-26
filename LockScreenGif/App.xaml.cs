using System.Diagnostics;
using System.Runtime.InteropServices;
using LockscreenGif.Activation;
using LockscreenGif.Contracts.Services;
using LockscreenGif.Helpers;
using LockscreenGif.Models;
using LockscreenGif.Notifications;
using LockscreenGif.Services;
using LockscreenGif.Services.Diagnostics;
using LockscreenGif.ViewModels;
using LockscreenGif.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace LockscreenGif;

// To learn more about WinUI 3, see https://docs.microsoft.com/windows/apps/winui/winui3/.
public partial class App : Application
{
    // The .NET Generic Host provides dependency injection, configuration, logging, and other services.
    // https://docs.microsoft.com/dotnet/core/extensions/generic-host
    // https://docs.microsoft.com/dotnet/core/extensions/dependency-injection
    // https://docs.microsoft.com/dotnet/core/extensions/configuration
    // https://docs.microsoft.com/dotnet/core/extensions/logging
    public IHost Host { get; }

    public static T GetService<T>()
        where T : class
    {
        if ((App.Current as App)!.Host.Services.GetService(typeof(T)) is not T service)
        {
            throw new ArgumentException($"{typeof(T)} needs to be registered in ConfigureServices within App.xaml.cs.");
        }

        return service;
    }

    public static WindowEx MainWindow { get; } = new MainWindow();

    public static UIElement? AppTitlebar { get; set; }

    public App()
    {
        InitializeComponent();

        Host = Microsoft
            .Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseContentRoot(AppContext.BaseDirectory)
            .ConfigureServices(
                (context, services) =>
                {
                    // Default Activation Handler
                    services.AddTransient<ActivationHandler<LaunchActivatedEventArgs>, DefaultActivationHandler>();

                    // Other Activation Handlers
                    services.AddTransient<IActivationHandler, AppNotificationActivationHandler>();

                    // Services
                    services.AddSingleton<IAppNotificationService, AppNotificationService>();
                    services.AddSingleton<IThemeSelectorService, ThemeSelectorService>();
                    services.AddSingleton<IActivationService, ActivationService>();
                    services.AddSingleton<IPageService, PageService>();
                    services.AddSingleton<INavigationService, NavigationService>();
                    services.AddSingleton<ILockscreenService, LockscreenService>();
                    services.AddSingleton<WindowsSessionMonitor>();
                    services.AddSingleton<PrivilegedSessionFactory>();
                    services.AddSingleton<DiagnosticsSessionService>();
                    services.AddTransient<DiagnosticsViewModel>();
                    services.AddTransient<DiagnosticsPage>();

                    // Views and ViewModels
                    services.AddTransient<MainViewModel>();
                    services.AddTransient<MainPage>();

                    // Configuration
                    services.Configure<LocalSettingsOptions>(context.Configuration.GetSection(nameof(LocalSettingsOptions)));
                }
            )
            .Build();

        App.GetService<IAppNotificationService>().Initialize();
        Logger.CleanupOldLogFiles();
        UnhandledException += App_UnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        Logger.Info($"App starting up. Version {BuildInfo.Version}. Windows {Environment.OSVersion}.");
        FfmpegService.CleanupTempDirectories();
        GifSkiService.CleanupTempDirectories();
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        HandleCrash(e.Exception, handledOnUIThread: true);
        e.Handled = true;
    }

    private void CurrentDomain_UnhandledException(object s, System.UnhandledExceptionEventArgs e)
    {
        HandleCrash((Exception)e.ExceptionObject, handledOnUIThread: false);
    }

    private void TaskScheduler_UnobservedTaskException(object? s, UnobservedTaskExceptionEventArgs e)
    {
        HandleCrash(e.Exception, handledOnUIThread: false);
        e.SetObserved();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);

        await App.GetService<IActivationService>().ActivateAsync(args);
        GetService<WindowsSessionMonitor>().Start(WindowNative.GetWindowHandle(MainWindow));
        MainWindow.AppWindow.Closing += MainWindow_Closing;
    }

    private bool _closePending;
    private bool _closingAllowed;

    private async void MainWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        var diagnostics = GetService<DiagnosticsSessionService>();
        var lockscreen = GetService<ILockscreenService>();
        if (_closingAllowed || (!diagnostics.IsRunning && !lockscreen.IsApplying))
        {
            GetService<WindowsSessionMonitor>().Dispose();
            MainWindow.AppWindow.Closing -= MainWindow_Closing;
            return;
        }
        args.Cancel = true;
        if (_closePending)
        {
            return;
        }

        _closePending = true;
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
            await diagnostics.CloseAsync("The app closed before the diagnostic test completed.");
        }
        catch (Exception ex)
        {
            Logger.Error("Could not finish the diagnostic session while closing", ex);
            diagnostics.Interrupt("The app closed while diagnostic shutdown encountered an error.");
        }
        finally
        {
            // Ordinary Lockscreen-page applies also finish their native operations first.
            await lockscreen.WaitForIdleAsync();
            _closingAllowed = true;
            MainWindow.Close();
        }
    }

    private static void CreateDump(Exception exception)
    {
        var dumpFilePath = Path.Combine(Logger.GetLogPath(), "CrashDump.dmp");

        using var fs = new FileStream(dumpFilePath, FileMode.Create);
        var process = Process.GetCurrentProcess();
        DumpCreator.MiniDumpWriteDump(
            process.Handle,
            (uint)process.Id,
            fs.SafeFileHandle,
            DumpCreator.Typ.MiniDumpNormal,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero
        );
    }

    private void HandleCrash(Exception ex, bool handledOnUIThread)
    {
        try
        {
            GetService<DiagnosticsSessionService>().Interrupt($"App error: {ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}");
        }
        catch (Exception recordingError)
        {
            Logger.Error("Could not checkpoint diagnostics during crash", recordingError);
        }
        try
        {
            CreateDump(ex);
        }
        catch (Exception dumpError)
        {
            Logger.Error("Could not create crash dump", dumpError);
        }
        Logger.Fatal("App encountered a fatal exception", ex);

        // Show a simple dialog – can't use MessageBox.Show from WinUI,
        // so call the Win32 API directly or use a ContentDialog.
        ShowDialog(ex);

        Environment.Exit(1);
    }

    private static void ShowDialog(Exception ex)
    {
        const uint MB_ICONERROR = 0x00000010u;
        const uint MB_OK = 0x00000000u;

        var hwnd = WindowNative.GetWindowHandle(MainWindow);
        _ = MessageBox(hwnd, $"An uncaught exception was thrown:\n\n{ex.Message}\n\n{ex.StackTrace}", "Fatal error", MB_ICONERROR | MB_OK);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
