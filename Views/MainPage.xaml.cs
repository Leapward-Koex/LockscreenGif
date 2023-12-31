﻿using LockscreenGif.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT;
using System.Runtime.InteropServices;
using LockscreenGif.Contracts.Services;
using LockscreenGif.Helpers;

namespace LockscreenGif.Views;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel
    {
        get;
    }

    private readonly ILockscreenService _lockscreenService;
    private readonly IAppNotificationService _notificationService;

    [ComImport, System.Runtime.InteropServices.Guid("3E68D4BD-7135-4D10-8018-9FB6D9F33FA1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IInitializeWithWindow
    {
        void Initialize([In] IntPtr hwnd);
    }

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Auto, PreserveSig = true, SetLastError = false)]
    public static extern IntPtr GetActiveWindow();

    public MainPage()
    {
        ViewModel = App.GetService<MainViewModel>();
        _lockscreenService = App.GetService<ILockscreenService>();
        _notificationService = App.GetService<IAppNotificationService>();
        InitializeComponent();
    }

    private async void OpenGifButton_click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        var initializeWithWindowWrapper = picker.As<IInitializeWithWindow>();
        initializeWithWindowWrapper.Initialize(GetActiveWindow());
        picker.ViewMode = PickerViewMode.Thumbnail;
        picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
        picker.FileTypeFilter.Add(".gif");

        var file = await picker.PickSingleFileAsync(); // https://github.com/microsoft/WindowsAppSDK/issues/2504 What
        if (file != null && file.ContentType == "image/gif")
        {
            _lockscreenService.CurrentImage = file;
            currentImage.Source = _lockscreenService.CurrentImageBitmap!;
            ApplyButton.IsEnabled = true;
        }
    }

    private async void SetLockscreenButton_click(Object sender, RoutedEventArgs e)
    {
        ApplyButton.IsEnabled = false;
        var success = await _lockscreenService.ApplyGifAsLockscreenAsync(); ;
        if (success)
        {
            _notificationService.Show(string.Format("AppNotificationSuccess".GetLocalized(), AppContext.BaseDirectory));
        }
        else
        {
            _notificationService.Show(string.Format("AppNotificationFailure".GetLocalized(), AppContext.BaseDirectory));
        }
    }
}
