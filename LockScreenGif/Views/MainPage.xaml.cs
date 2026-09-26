using System.Globalization;
using System.Runtime.InteropServices;
using LockscreenGif.Contracts.Services;
using LockscreenGif.Helpers;
using LockscreenGif.Services;
using LockscreenGif.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Editing;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT;

namespace LockscreenGif.Views;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel { get; }

    private readonly ILockscreenService _lockscreenService;
    private readonly IAppNotificationService _notificationService;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _lockscreenModeTimer;
    private LockscreenService.LockScreenMode _lastLockScreenMode = LockscreenService.LockScreenMode.Unknown;

    private StorageFile? _videoFile;
    private uint _videoWidth;
    private uint _videoHeight;
    private double? _videoFps;
    private MediaPlaybackSession? _session;

    private void Seek(double sec)
    {
        if (_session != null)
        {
            _session.Position = TimeSpan.FromSeconds(sec);
        }
    }

    public MainPage()
    {
        ViewModel = App.GetService<MainViewModel>();
        _lockscreenService = App.GetService<ILockscreenService>();
        _notificationService = App.GetService<IAppNotificationService>();
        InitializeComponent();

        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        Loaded += (_, _) => StartLockscreenModePolling();

        Unloaded += (_, _) =>
        {
            StopLockscreenModePolling();
            PausePreview();
            _videoLoadCts?.Cancel();
        };
        App.MainWindow.Closed += (_, _) =>
        {
            StopPlaybackRendering();
            if (_session is not null)
            {
                _session.SeekCompleted -= Session_SeekCompleted;
            }

            _videoLoadCts?.Cancel();
            _previewCts?.Cancel();
            _editorCts?.Cancel();
            VideoPreview.MediaPlayer?.Dispose();
        };
    }

    private void StartLockscreenModePolling()
    {
        _lockscreenModeTimer ??= DispatcherQueue.CreateTimer();
        _lockscreenModeTimer.Interval = TimeSpan.FromSeconds(5);
        _lockscreenModeTimer.IsRepeating = true;
        _lockscreenModeTimer.Tick -= LockscreenModeTimer_Tick;
        _lockscreenModeTimer.Tick += LockscreenModeTimer_Tick;

        RefreshLockscreenModeUi();
        _lockscreenModeTimer.Start();
    }

    private void StopLockscreenModePolling()
    {
        if (_lockscreenModeTimer is null)
        {
            return;
        }

        _lockscreenModeTimer.Stop();
        _lockscreenModeTimer.Tick -= LockscreenModeTimer_Tick;
        _lockscreenModeTimer = null;
    }

    private void LockscreenModeTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        RefreshLockscreenModeUi();
    }

    private void RefreshLockscreenModeUi()
    {
        var mode = LockscreenService.TryGetLockScreenMode();
        if (mode == _lastLockScreenMode)
        {
            return;
        }

        _lastLockScreenMode = mode;

        var ok = mode is LockscreenService.LockScreenMode.PictureOrOther;

        if (PrereqWarningIcon != null)
        {
            PrereqWarningIcon.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        }

        if (LockscreenModeWarning != null)
        {
            LockscreenModeWarning.IsOpen = !ok;
        }

        if (PrereqStep1Badge != null)
        {
            PrereqStep1Badge.Background = ok
                ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                : new SolidColorBrush(Colors.OrangeRed);
        }

        if (PrereqStep1Title != null)
        {
            PrereqStep1Title.Foreground = ok
                ? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
                : new SolidColorBrush(Colors.OrangeRed);
        }

        if (PrereqStep1Body != null)
        {
            PrereqStep1Body.Foreground = ok
                ? (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                : new SolidColorBrush(Colors.OrangeRed);
        }
    }

    static double RoundToSigFigs(double value, int digits = 2)
    {
        if (value == 0)
        {
            return 0;
        }

        var abs = Math.Abs(value);
        var exponent = (int)Math.Floor(Math.Log10(abs)); // 5329 → 3
        var scale = Math.Pow(10, exponent - digits + 1); // 10^(3-2+1)=100
        return Math.Round(value / scale) * scale; // → 5300
    }

    private void UpdateFileSizeWarning()
    {
        if (ComboResolution.SelectedItem is not ComboBoxItem || ComboFps.SelectedItem is not ComboBoxItem)
        {
            return;
        }

        var width = (int)((ComboBoxItem)ComboResolution.SelectedItem).Tag; // e.g. 480, 720, 1080
        var fps = (double?)((ComboBoxItem)ComboFps.SelectedItem).Tag ?? 0;

        //   * preserve the source aspect ratio
        //   * assume 24-bit RGB                    → 3 bytes / pixel
        //   * assume PNG compresses to ~35 %       → factor 0.35 (empirical)
        var height = (int)Math.Round(width * (double)_videoHeight / Math.Max(1, _videoWidth));
        var bytesPerFrame =
            width
            * height
            * 3 /*RGB*/
            * 0.35;
        var kbPerFrame = bytesPerFrame / 1024.0;

        var durationSec = _endSec - _startSec;
        var frameCount = fps == 0 ? _endFrame - _startFrame : Math.Min(_endFrame - _startFrame, Math.Ceiling(durationSec * fps) + 1);
        var totalMB = frameCount * kbPerFrame / 1024.0;

        var rounded = RoundToSigFigs(totalMB, 2);

        FileSizeWarning.Message =
            $"Generating the GIF may temporarily take up to {rounded} MB of space to generate the GIF. "
            + "Ensure you have enough space free.";
        FileSizeWarning.IsOpen = true;
        if (totalMB > 5000)
        {
            FileSizeWarning.Severity = InfoBarSeverity.Error;
        }
        else if (totalMB > 1000)
        {
            FileSizeWarning.Severity = InfoBarSeverity.Warning;
        }
        else
        {
            FileSizeWarning.Severity = InfoBarSeverity.Informational;
        }
    }

    private void HideVideoUi()
    {
        VideoPreviewPanel.Visibility = Visibility.Collapsed;
        TrimControlsPanel.Visibility = Visibility.Collapsed;
        ComboSettingsStack.Visibility = Visibility.Collapsed;
        ComboFps.Visibility = Visibility.Collapsed;
        FileSizeWarning.IsOpen = false;
    }

    private void ShowVideoUi()
    {
        VideoPreviewPanel.Visibility = Visibility.Visible;
        TrimControlsPanel.Visibility = Visibility.Visible;
        ComboSettingsStack.Visibility = Visibility.Visible;
        ComboFps.Visibility = Visibility.Visible;
    }

    private void PopulateResolutionList()
    {
        ComboResolution.Items.Clear();
        ComboResolution.Items.Add(new ComboBoxItem { Content = $"Original ({_videoHeight}p)", Tag = (int)_videoWidth });
        if (_videoWidth > 2560)
        {
            ComboResolution.Items.Add(new ComboBoxItem { Content = "1440p", Tag = 2560 });
        }
        if (_videoWidth > 1920)
        {
            ComboResolution.Items.Add(new ComboBoxItem { Content = "1080p", Tag = 1920 });
        }
        if (_videoWidth > 1280)
        {
            ComboResolution.Items.Add(new ComboBoxItem { Content = "720p", Tag = 1280 });
        }
        if (_videoWidth > 854)
        {
            ComboResolution.Items.Add(new ComboBoxItem { Content = "480p", Tag = 854 });
        }
        ComboResolution.SelectedIndex = 0;
    }

    private void PopulateFpsList()
    {
        ComboFps.Items.Clear();
        ComboFps.Items.Add(new ComboBoxItem { Content = "All source frames", Tag = 0.0 });
        if (_videoFps > 30)
        {
            ComboFps.Items.Add(new ComboBoxItem { Content = "30 fps target", Tag = 30.0 });
        }
        if (_videoFps > 15)
        {
            ComboFps.Items.Add(new ComboBoxItem { Content = "15 fps target", Tag = 15.0 });
        }
        if (_videoFps > 10)
        {
            ComboFps.Items.Add(new ComboBoxItem { Content = "10 fps target", Tag = 10.0 });
        }
        if (_videoFps > 5)
        {
            ComboFps.Items.Add(new ComboBoxItem { Content = "5 fps target", Tag = 5.0 });
        }
        ComboFps.SelectedIndex = 0;
    }

    private async void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        if (
            _videoFile == null
            || _frames is null
            || !_mediaReady
            || _actionPending
            || !CommitTime(StartTimeTextBox)
            || !CommitTime(EndTimeTextBox)
        )
        {
            return;
        }

        try
        {
            _actionPending = true;
            PausePreview();
            TrimEditorHost.IsEnabled = false;
            VideoSettingsHost.IsEnabled = false;
            ClearGeneratedGif();
            ApplyButton.IsEnabled = false;
            removeAnimatedLockscreen.IsEnabled = false;
            GenerateButton.IsEnabled = false;
            GenerateLoading.ShowError = false;
            GenerateLoading.Value = 0;
            GenerateLoading.IsIndeterminate = true;
            GenerateLoading.Visibility = Visibility.Visible;

            var ExtractFramesProgress = (double percent) =>
            {
                var display = percent * 0.3;
                DispatcherQueue.TryEnqueue(() =>
                {
                    GenerateLoading.IsIndeterminate = false;
                    GenerateLoading.Value = display;
                });
            };

            var CreateGifProgress = (double percent) =>
            {
                var display = 30 + percent * 0.7;
                DispatcherQueue.TryEnqueue(() =>
                {
                    GenerateLoading.IsIndeterminate = false;
                    GenerateLoading.Value = display;
                });
            };

            var chosenWidth = (int)((ComboBoxItem)ComboResolution.SelectedItem).Tag;
            var chosenFps = (double)((ComboBoxItem)ComboFps.SelectedItem).Tag;

            var extracted = await VideoFrameService.ExportAsync(
                _videoFile.Path,
                _frames,
                _startFrame,
                _endFrame,
                chosenWidth,
                chosenFps,
                ExtractFramesProgress
            );
            var gifLocation = await GifSkiService.CreateGif(
                extracted.Directory,
                CreateGifProgress,
                extracted.Timestamps,
                _endSec - _startSec
            );

            _lockscreenService.CurrentImage = await StorageFile.GetFileFromPathAsync(gifLocation);
            currentImage.Source = _lockscreenService.CurrentImageBitmap!;
            _generatedGif = _lockscreenService.CurrentImage;
            _generatedGifName = Path.GetFileNameWithoutExtension(_videoFile.Name);
            SaveGeneratedGifPanel.Visibility = Visibility.Visible;
            ApplyButton.IsEnabled = true;
            GenerateLoading.Value = 100;
        }
        catch (Exception ex)
        {
            GenerateLoading.ShowError = true;
            OperationStatus.Title = "GIF generation failed";
            OperationStatus.Message = "The selected clip could not be converted. Try another selection or video.";
            OperationStatus.IsOpen = true;
            Logger.Error("Failed to create gif from video", ex);
        }
        finally
        {
            _actionPending = false;
            ApplyButton.IsEnabled = _lockscreenService.CurrentImage is not null;
            removeAnimatedLockscreen.IsEnabled = true;
            FfmpegService.CleanupTempDirectories();
            TrimEditorHost.IsEnabled = true;
            VideoSettingsHost.IsEnabled = true;
            GenerateLoading.IsIndeterminate = false;
            UpdateGenerateEnabled();
        }

        return;
    }

    public static async Task<(double? Fps, uint Width, uint Height)> GetVideoInfoAsync(StorageFile file)
    {
        if (file is null)
        {
            Logger.Error("File missing");
            return (null, 0, 0);
        }

        try
        {
            var clip = await MediaClip.CreateFromFileAsync(file);
            var props = clip.GetVideoEncodingProperties();

            double? fps = props.FrameRate.Denominator == 0 ? null : (double)props.FrameRate.Numerator / props.FrameRate.Denominator;

            return (fps, props.Width, props.Height);
        }
        catch (Exception ex)
        {
            Logger.Error("failed to get video info", ex);
        }
        return (null, 0, 0);
    }

    private void ComboResolution_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComboFps.SelectedItem != null && ComboResolution.SelectedItem != null)
        {
            UpdateFileSizeWarning();
        }
    }
}
