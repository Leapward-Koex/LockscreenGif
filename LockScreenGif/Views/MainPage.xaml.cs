using LockscreenGif.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Storage.Pickers;
using WinRT;
using System.Runtime.InteropServices;
using LockscreenGif.Contracts.Services;
using LockscreenGif.Helpers;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using CommunityToolkit.WinUI.Controls;
using System.Globalization;
using LockscreenGif.Services;
using Windows.Media.Editing;

namespace LockscreenGif.Views;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel
    {
        get;
    }

    private readonly ILockscreenService _lockscreenService;
    private readonly IAppNotificationService _notificationService;

    private StorageFile? _videoFile;
    private double _startSec;
    private double _endSec;
    private uint _videoWidth;
    private uint _videoHeight;
    private double? _videoFps;
    private bool _correctingPosition;
    private MediaPlaybackSession? _session;
    private void Seek(double sec)
    {
        if (_session != null)
        {
            _session.Position = TimeSpan.FromSeconds(sec);
        }
    }

    // COM interop for file pickers
    [ComImport, Guid("3E68D4BD-7135-4D10-8018-9FB6D9F33FA1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IInitializeWithWindow
    {
        void Initialize([In] IntPtr hwnd);
    }

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Auto, PreserveSig = true)]
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
        picker.As<IInitializeWithWindow>().Initialize(GetActiveWindow());
        picker.ViewMode = PickerViewMode.Thumbnail;
        picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
        picker.FileTypeFilter.Add(".gif");

        var file = await picker.PickSingleFileAsync();
        if (file != null && file.ContentType == "image/gif")
        {
            _lockscreenService.CurrentImage = file;
            currentImage.Source = _lockscreenService.CurrentImageBitmap!;
            ApplyButton.IsEnabled = true;
        }
    }

    static double RoundToSigFigs(double value, int digits = 2)
    {
        if (value == 0)
        {
            return 0;
        }

        var abs = Math.Abs(value);
        var exponent = (int)Math.Floor(Math.Log10(abs));          // 5329 → 3
        var scale = Math.Pow(10, exponent - digits + 1);       // 10^(3-2+1)=100
        return Math.Round(value / scale) * scale;                    // → 5300
    }

    private void UpdateFileSizeWarning()
    {
        var width = (int)((ComboBoxItem)ComboResolution.SelectedItem).Tag;   // e.g. 480, 720, 1080
        var fps = (double?)((ComboBoxItem)ComboFps.SelectedItem).Tag ?? 0;

        //   * assume the source is 16:9            → height = width / 16 * 9
        //   * assume 24-bit RGB                    → 3 bytes / pixel
        //   * assume PNG compresses to ~35 %       → factor 0.35 (empirical)
        var height = (int)Math.Round(width * 9.0 / 16.0);
        var bytesPerFrame = width * height * 3 /*RGB*/ * 0.35;
        var kbPerFrame = bytesPerFrame / 1024.0;

        var durationSec = _endSec - _startSec;
        var frameCount = durationSec * fps;
        var totalMB = frameCount * kbPerFrame / 1024.0;

        var rounded = RoundToSigFigs(totalMB, 2);


        FileSizeWarning.Message =
            $"Generating the GIF may temporarily take up to {rounded} MB of space to generate the GIF. " +
            "Ensure you have enough space free.";
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

    private async void SetLockscreenButton_click(object sender, RoutedEventArgs e)
    {
        ApplyButton.IsEnabled = false;
        Logger.Info("Trying to set lockscreen");
        var success = await _lockscreenService.ApplyGifAsLockscreenAsync();
        GifSkiService.CleanupTempDirectories();
        if (success)
        {
            _notificationService.Show(string.Format("AppNotificationSuccess".GetLocalized(), AppContext.BaseDirectory));
        }
        else
        {
            _notificationService.Show(string.Format("AppNotificationFailure".GetLocalized(), AppContext.BaseDirectory));
        }
    }

    private async void RemoveAnimatedLockscreenButton_click(object sender, RoutedEventArgs e)
    {
        Logger.Info("Trying to delete applied animated lockscreen");
        var result = await _lockscreenService.RemoveAppliedGif();
        if (result == null)
        {
            _notificationService.Show(string.Format("AppNotificationDeleteFailure".GetLocalized(), AppContext.BaseDirectory));
        }
        else if (result.FailedDeletions != 0)
        {
            _notificationService.Show(string.Format("AppNotificationDeletePartialFailure".GetLocalized(), AppContext.BaseDirectory, result.SuccessfulDeletions, result.FailedDeletions));
        }
        else
        {
            _notificationService.Show(string.Format("AppNotificationDeleteSuccess".GetLocalized(), AppContext.BaseDirectory));
        }
    }

    private async void OpenVideoButton_click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.As<IInitializeWithWindow>().Initialize(GetActiveWindow());
        picker.ViewMode = PickerViewMode.Thumbnail;
        picker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
        picker.FileTypeFilter.Add(".mp4");
        picker.FileTypeFilter.Add(".mkv");

        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            _videoFile = file;
            (_videoFps, _videoWidth, _videoHeight) = await GetVideoInfoAsync(file);

            PopulateResolutionList();
            PopulateFpsList();

            ShowVideoUi();
            GenerateLoading.Visibility = Visibility.Collapsed;


            VideoPreview.SetMediaPlayer(new MediaPlayer());
            VideoPreview.MediaPlayer.MediaOpened += VideoPreview_MediaOpened;
            _session = VideoPreview.MediaPlayer.PlaybackSession;
            _session.PositionChanged += Session_PositionChanged;
            VideoPreview.MediaPlayer.IsMuted = true;
            VideoPreview.MediaPlayer.Source = MediaSource.CreateFromStorageFile(file);

            GenerateButton.IsEnabled = false;
        }
    }

    private void HideVideoUi()
    {
        VideoPreview.Visibility = Visibility.Collapsed;
        TrimControlsPanel.Visibility = Visibility.Collapsed;
        ComboSettingsStack.Visibility = Visibility.Collapsed;
        ComboFps.Visibility = Visibility.Collapsed;
        FileSizeWarning.IsOpen = false;
    }

    private void ShowVideoUi()
    {
        VideoPreview.Visibility = Visibility.Visible;
        TrimControlsPanel.Visibility = Visibility.Visible;
        ComboSettingsStack.Visibility = Visibility.Visible;
        ComboFps.Visibility = Visibility.Visible;
    }

    private void PopulateResolutionList()
    {
        ComboResolution.Items.Clear();
        ComboResolution.Items.Add(new ComboBoxItem
        {
            Content = $"Original ({_videoHeight}p)",
            Tag = (int)_videoWidth
        });
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
        ComboFps.Items.Add(new ComboBoxItem
        {
            Content = $"Original ({_videoFps:F2} fps)",
            Tag = _videoFps
        });
        if (_videoFps > 30)
        {
            ComboFps.Items.Add(new ComboBoxItem { Content = "30 fps", Tag = 30.0 });
        }
        if (_videoFps > 15)
        {
            ComboFps.Items.Add(new ComboBoxItem { Content = "15 fps", Tag = 15.0 });
        }
        if (_videoFps > 10)
        {
            ComboFps.Items.Add(new ComboBoxItem { Content = "10 fps", Tag = 10.0 });
        }
        if (_videoFps > 5)
        {
            ComboFps.Items.Add(new ComboBoxItem { Content = "5 fps", Tag = 5.0 });
        }
        ComboFps.SelectedIndex = 0;
    }

    private void StartTimeTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (TryParseTime(StartTimeTextBox.Text, out var sec))
        {
            sec = Math.Max(0, Math.Min(sec, TrimSelector.Maximum));
            _startSec = sec;
            TrimSelector.RangeStart = _startSec;
            StartTimeTextBox.Text = TimeSpan.FromSeconds(_startSec)
                                       .ToString(@"mm\:ss\.f", CultureInfo.InvariantCulture);
            if (_session?.Position.TotalSeconds < _startSec)
            {
                Seek(_startSec);
            }
        }
        else
        {
            StartTimeTextBox.Text = TimeSpan.FromSeconds(_startSec)
                                       .ToString(@"mm\:ss\.f", CultureInfo.InvariantCulture);
        }
        UpdateFileSizeWarning();

    }

    private void EndTimeTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (TryParseTime(EndTimeTextBox.Text, out var sec))
        {
            sec = Math.Max(0, Math.Min(sec, TrimSelector.Maximum));
            _endSec = sec;
            TrimSelector.RangeEnd = _endSec;
            EndTimeTextBox.Text = TimeSpan.FromSeconds(_endSec)
                                     .ToString(@"mm\:ss\.f", CultureInfo.InvariantCulture);
            if (_session?.Position.TotalSeconds > _endSec)
            {
                Seek(_startSec);
            }
        }
        else
        {
            EndTimeTextBox.Text = TimeSpan.FromSeconds(_endSec)
                                     .ToString(@"mm\:ss\.f", CultureInfo.InvariantCulture);
        }
        UpdateFileSizeWarning();

    }

    private void VideoPreview_MediaOpened(MediaPlayer sender, object args)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            var duration = sender.PlaybackSession.NaturalDuration.TotalSeconds;
            if (_videoFps == null || _videoFps == 0 || duration == 0 || VideoPreview.MediaPlayer.NaturalDuration == TimeSpan.MaxValue)
            {
                Logger.Error("Failed to read video data");
                // TODO remux with ffmpeg -i input.mp4 -c copy -movflags +faststart fixed.mp4
                _notificationService.Show(string.Format("AppVideoLoadNotificationFailure".GetLocalized(), AppContext.BaseDirectory));
                HideVideoUi();
                return;
            }

            TrimSelector.Minimum = 0;
            TrimSelector.Maximum = duration;
            TrimSelector.RangeStart = 0;
            TrimSelector.RangeEnd = duration;
            TrimSelector.StepFrequency = 0.1;

            _startSec = 0;
            _endSec = duration;

            // initialize the TextBox values (with .f)
            StartTimeTextBox.Text = TimeSpan.FromSeconds(0)
                                       .ToString(@"mm\:ss\.f", CultureInfo.InvariantCulture);
            EndTimeTextBox.Text = TimeSpan.FromSeconds(duration)
                                       .ToString(@"mm\:ss\.f", CultureInfo.InvariantCulture);

            GenerateButton.IsEnabled = true;
            UpdateFileSizeWarning();
        });
    }

    private void TrimSelector_ValueChanged(object sender, RangeChangedEventArgs e)
    {
        if (e.ChangedRangeProperty == RangeSelectorProperty.MinimumValue)
        {
            _startSec = e.NewValue;
        }
        else
        {
            _endSec = e.NewValue;
        }

        // update the text inputs in sync
        StartTimeTextBox.Text = TimeSpan.FromSeconds(_startSec)
                                   .ToString(@"mm\:ss\.f", CultureInfo.InvariantCulture);
        EndTimeTextBox.Text = TimeSpan.FromSeconds(_endSec)
                                   .ToString(@"mm\:ss\.f", CultureInfo.InvariantCulture);
        UpdateFileSizeWarning();

    }

    private void TrimSelector_RangeDragging(object sender, CustomControls.RangeDraggingEventArgs e)
    {
        Seek(e.NewValue);
    }

    private void TrimSelector_ThumbDragStarted(object sender, DragStartedEventArgs e)
    {
        VideoPreview.MediaPlayer.Pause();
        _correctingPosition = true;
    }

    private void TrimSelector_ThumbDragCompleted(object sender, DragCompletedEventArgs e)
    {
        Seek(_startSec);
        VideoPreview.MediaPlayer.Play();
        _correctingPosition = false;
    }
    

    private void Session_PositionChanged(MediaPlaybackSession sender, object args)
    {
        if (_correctingPosition)
        {
            return;
        }

        var sec = sender.Position.TotalSeconds;

        if (sec < _startSec || sec > _endSec)
        {
            _correctingPosition = true;
            if (sec < _startSec)
            {
                Seek(_startSec);
                _correctingPosition = false;

            }
            else if (sec > _endSec)
            {
                Seek(_startSec);
                _correctingPosition = false;
            }
        }
    }

    private bool TryParseTime(string text, out double secs)
    {
        secs = 0;
        var parts = text.Split(':');
        if (parts.Length == 2
            && int.TryParse(parts[0], out var mins)
            && double.TryParse(parts[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var s))
        {
            secs = mins * 60 + s;
            return true;
        }
        return false;
    }

    private async void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_videoFile == null)
        {
            return;
        }

        try
        {
            GenerateButton.IsEnabled = false;
            GenerateLoading.Value = 0;
            GenerateLoading.IsIndeterminate = true;
            GenerateLoading.Visibility = Visibility.Visible;
            var trimmedVideo = await FfmpegService.TrimVideoAsync(_videoFile.Path, TimeSpan.FromSeconds(_startSec), TimeSpan.FromSeconds(_endSec));
            GenerateLoading.IsIndeterminate = false;
            var ExtractFramesProgress = (double percent) =>
            {
                var display = percent * 0.3;
                DispatcherQueue.TryEnqueue(() =>
                    GenerateLoading.Value = display
                );
            };

            var CreateGifProgress = (double percent) =>
            {
                var display = 30 + percent * 0.7;
                DispatcherQueue.TryEnqueue(() =>
                    GenerateLoading.Value = display
                );
            };

            var chosenWidth = (int)((ComboBoxItem)ComboResolution.SelectedItem).Tag;
            var chosenFps = (double)((ComboBoxItem)ComboFps.SelectedItem).Tag;

            var framesDir = await FfmpegService.ExtractPngFramesAsync(trimmedVideo, ExtractFramesProgress, chosenWidth, chosenFps, TimeSpan.FromSeconds(_endSec - _startSec));
            var gifLocation = await GifSkiService.CreateGif(framesDir, CreateGifProgress, chosenFps);


            _lockscreenService.CurrentImage = await StorageFile.GetFileFromPathAsync(gifLocation);
            currentImage.Source = _lockscreenService.CurrentImageBitmap!;
            ApplyButton.IsEnabled = true;
            GenerateLoading.Value = 100;
        }
        catch (Exception ex)
        {
            GenerateLoading.ShowError = true;
            Logger.Error("Failed to create gif from video", ex);
        }
        finally
        {
            FfmpegService.CleanupTempDirectories();
            GenerateButton.IsEnabled = true;
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

            double? fps = props.FrameRate.Denominator == 0
                                  ? null
                                  : (double)props.FrameRate.Numerator / props.FrameRate.Denominator;

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
        if (ComboFps.SelectedItem != null && ComboResolution.SelectedItem != null) {
            UpdateFileSizeWarning();
        }
    }
}
