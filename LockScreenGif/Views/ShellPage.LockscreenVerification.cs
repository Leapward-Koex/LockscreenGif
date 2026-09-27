using LockscreenGif.Contracts.Services;
using LockscreenGif.Helpers;
using LockscreenGif.Models;
using LockscreenGif.Services.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LockscreenGif.Views;

public sealed partial class ShellPage
{
    private readonly CancellationTokenSource _lockscreenFeedbackLifetime = new();
    private ContentDialog? _applyLockDialog;

    public async Task CompleteLockscreenApplyAsync(LockscreenApplyResult apply, string sourcePath)
    {
        var token = _lockscreenFeedbackLifetime.Token;
        if (token.IsCancellationRequested)
        {
            return;
        }

        LockscreenVerificationStatus.IsOpen = false;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = (XamlRoot.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default,
            Title = "GIF applied. Lock your screen now?",
            Content = new TextBlock
            {
                Text = "Lock now to check whether Windows reads the copied GIF. Unlock to see the result. You can also lock later.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Lock now",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Close,
        };
        _applyLockDialog = dialog;
        try
        {
            var choice = await dialog.ShowAsync();
            _applyLockDialog = null;
            token.ThrowIfCancellationRequested();
            if (choice == ContentDialogResult.Primary)
            {
                var diagnostics = App.GetService<DiagnosticsSessionService>();
                if (diagnostics.IsRunning)
                {
                    ShowLockscreenVerificationStatus(
                        "File-read check unavailable",
                        "A diagnostic test is running. Finish it before checking another lock screen.",
                        InfoBarSeverity.Warning
                    );
                }
                else
                {
                    ShowLockscreenVerificationStatus(
                        "Preparing the file-read check…",
                        "Your screen will lock when monitoring is ready. Windows may ask for administrator permission.",
                        InfoBarSeverity.Informational
                    );
                    var verification = App.GetService<LockscreenVerificationService>();
                    void OnVerificationChanged(object? sender, EventArgs args) =>
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (!token.IsCancellationRequested && verification.IsRunning)
                            {
                                LockscreenVerificationStatus.Title = verification.Status;
                                LockscreenVerificationStatus.Message = "Monitoring the copied GIF. Unlock to see the result.";
                            }
                        });
                    verification.Changed += OnVerificationChanged;
                    LockscreenVerificationResult result;
                    try
                    {
                        result = await verification.VerifyAndLockAsync(apply, sourcePath);
                    }
                    finally
                    {
                        verification.Changed -= OnVerificationChanged;
                    }
                    token.ThrowIfCancellationRequested();
                    ShowLockscreenVerificationStatus(
                        result.Title,
                        result.Message,
                        result.ReadConfirmed ? InfoBarSeverity.Success : InfoBarSeverity.Warning
                    );
                }

                // Give the compact in-app result time to appear before the Windows notification.
                await Task.Delay(TimeSpan.FromSeconds(3), token);
                if (LockscreenVerificationStatus.Severity == InfoBarSeverity.Success)
                {
                    LockscreenVerificationStatus.IsOpen = false;
                }
            }

            token.ThrowIfCancellationRequested();
            App.GetService<IAppNotificationService>()
                .Show(string.Format("AppNotificationSuccess".GetLocalized(), AppContext.BaseDirectory));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            _applyLockDialog = null;
        }
    }

    private void ShowLockscreenVerificationStatus(string title, string message, InfoBarSeverity severity)
    {
        LockscreenVerificationStatus.Title = title;
        LockscreenVerificationStatus.Message = message;
        LockscreenVerificationStatus.Severity = severity;
        LockscreenVerificationStatus.IsOpen = true;
    }

    public void StopLockscreenFeedback()
    {
        _lockscreenFeedbackLifetime.Cancel();
        _applyLockDialog?.Hide();
    }
}
