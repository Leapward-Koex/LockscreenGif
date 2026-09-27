using System.Diagnostics;
using System.Globalization;
using LockscreenGif.Services;
using LockscreenGif.Services.Analytics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace LockscreenGif.Views;

public sealed partial class SettingsPage
{
    private bool _logActionPending;

    private async void OpenLogFolder_Click(object sender, RoutedEventArgs args)
    {
        if (_logActionPending)
        {
            return;
        }

        SetLogActionPending(true);
        try
        {
            var path = await Task.Run(Logger.GetLogPath);
            var folder = await StorageFolder.GetFolderFromPathAsync(path);
            if (!await Launcher.LaunchFolderAsync(folder))
            {
                throw new IOException("Windows could not open the log folder.");
            }
        }
        catch (Exception ex)
        {
            ShowLogStatus("Could not open the log folder", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetLogActionPending(false);
        }
    }

    private async void SaveLogs_Click(object sender, RoutedEventArgs args)
    {
        if (_logActionPending)
        {
            return;
        }

        SetLogActionPending(true);
        string? temporaryPath = null;
        Stopwatch? timer = null;
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                SuggestedFileName = "LockscreenGif-logs-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture),
                DefaultFileExtension = ".zip",
            };
            picker.FileTypeChoices.Add("ZIP archive", new List<string> { ".zip" });
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            var destination = await picker.PickSaveFileAsync();
            if (destination is null)
            {
                TrackLogExport(AnalyticsOutcome.Cancelled, timer);
                return;
            }
            if (!string.Equals(Path.GetExtension(destination.Name), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Choose a file name ending in .zip.");
            }

            timer = Stopwatch.StartNew();
            LogExportProgress.Visibility = Visibility.Visible;
            LogExportRing.IsActive = true;
            var logDirectory = await Task.Run(Logger.GetLogPath);
            temporaryPath = Path.Combine(Path.GetTempPath(), "LockscreenGif-logs-" + Guid.NewGuid().ToString("N") + ".zip");
            var count = await LogArchiveWriter.WriteAsync(logDirectory, temporaryPath);
            var archive = await StorageFile.GetFileFromPathAsync(temporaryPath);
            await PickedFileWriter.CopyAsync(archive, destination);
            ShowLogStatus(
                "Logs saved",
                $"Saved {count} log {(count == 1 ? "file" : "files")} to {destination.Path}",
                InfoBarSeverity.Success
            );
            TrackLogExport(AnalyticsOutcome.Succeeded, timer);
        }
        catch (Exception ex)
        {
            ShowLogStatus("Could not save logs", ex.Message, InfoBarSeverity.Error);
            TrackLogExport(ex is OperationCanceledException ? AnalyticsOutcome.Cancelled : AnalyticsOutcome.Failed, timer, ex);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    await Task.Run(() => File.Delete(temporaryPath));
                }
                catch { }
            }
            SetLogActionPending(false);
        }
    }

    private void TrackLogExport(AnalyticsOutcome outcome, Stopwatch? timer, Exception? error = null) =>
        _analytics?.Track(
            AnalyticsEvent.LogExportCompleted,
            new AnalyticsProperties
            {
                Outcome = outcome,
                DurationMs = timer?.Elapsed.TotalMilliseconds,
                ErrorKind = error is null ? null : AnalyticsProperties.ClassifyError(error),
            }
        );

    private void SetLogActionPending(bool pending)
    {
        _logActionPending = pending;
        OpenLogFolderButton.IsEnabled = SaveLogsButton.IsEnabled = !pending;
        if (pending)
        {
            LogsStatus.IsOpen = false;
        }
        else
        {
            LogExportRing.IsActive = false;
            LogExportProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowLogStatus(string title, string message, InfoBarSeverity severity)
    {
        LogsStatus.Title = title;
        LogsStatus.Message = message;
        LogsStatus.Severity = severity;
        LogsStatus.IsOpen = true;
    }
}
