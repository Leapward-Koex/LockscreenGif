using System.Diagnostics;
using System.Runtime.InteropServices;
using LockscreenGif.Activation;
using LockscreenGif.Contracts.Services;
using LockscreenGif.Models;
using LockscreenGif.Notifications;
using LockscreenGif.Services;
using LockscreenGif.ViewModels;
using LockscreenGif.Views;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
    public IHost Host
    {
        get;
    }

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

        Host = Microsoft.Extensions.Hosting.Host.
        CreateDefaultBuilder().
        UseContentRoot(AppContext.BaseDirectory).
        ConfigureServices((context, services) =>
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

            // Views and ViewModels
            services.AddTransient<MainViewModel>();
            services.AddTransient<MainPage>();

            // Configuration
            services.Configure<LocalSettingsOptions>(context.Configuration.GetSection(nameof(LocalSettingsOptions)));
        }).
        Build();

        App.GetService<IAppNotificationService>().Initialize();
        Logger.CleanupOldLogFiles();
        UnhandledException += App_UnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        Logger.Info($"App starting up. Running on Windows {Environment.OSVersion}");
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

    protected async override void OnLaunched(LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);

        await App.GetService<IActivationService>().ActivateAsync(args);
    }

    private static void CreateDump(Exception exception)
    {
        var dumpFilePath = Path.Combine(Logger.GetLogPath(), "CrashDump.dmp");

        using var fs = new FileStream(dumpFilePath, FileMode.Create);
        var process = Process.GetCurrentProcess();
        DumpCreator.MiniDumpWriteDump(process.Handle, (uint)process.Id, fs.SafeFileHandle, DumpCreator.Typ.MiniDumpNormal, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }

    private void HandleCrash(Exception ex, bool handledOnUIThread)
    {
        CreateDump(ex);
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
        _ = MessageBox(hwnd,
                   $"An uncaught exception was thrown:\n\n{ex.Message}\n\n{ex.StackTrace}",
                   "Fatal error",
                   MB_ICONERROR | MB_OK);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
