using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LockscreenGif.Views.Dialogs;

/// <summary>A visible, cancellable countdown. The caller performs the lock after the dialog closes.</summary>
internal sealed class DiagnosticLockDialog
{
    private readonly ContentDialog _dialog;
    private readonly DispatcherQueueTimer _timer;
    private int _seconds = 5;
    private bool _expired;
    private bool _cancelled;
    private bool _opened;

    public DiagnosticLockDialog(XamlRoot root, DispatcherQueue dispatcher)
    {
        _dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = (root.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default,
            Title = "Locking screen in 5…",
            Content = new TextBlock
            {
                Text = "Check whether the GIF animates, then unlock to review the results.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Lock now",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.IsRepeating = true;
    }

    public async Task<bool> ShowAsync()
    {
        if (_cancelled)
        {
            return false;
        }

        _dialog.Opened += OnOpened;
        _dialog.Closing += OnClosing;
        _timer.Tick += OnTick;
        try
        {
            var result = await _dialog.ShowAsync();
            return !_cancelled && (_expired || result == ContentDialogResult.Primary);
        }
        finally
        {
            _timer.Stop();
            _opened = false;
            _timer.Tick -= OnTick;
            _dialog.Opened -= OnOpened;
            _dialog.Closing -= OnClosing;
        }
    }

    public void Cancel()
    {
        _cancelled = true;
        _timer.Stop();
        if (_opened)
        {
            _dialog.Hide();
        }
    }

    private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        _opened = true;
        if (_cancelled)
        {
            _dialog.Hide();
        }
        else
        {
            _timer.Start();
        }
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        _timer.Stop();
        _opened = false;
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        if (_cancelled || !_opened)
        {
            return;
        }

        if (--_seconds > 0)
        {
            _dialog.Title = $"Locking screen in {_seconds}…";
        }
        else
        {
            _timer.Stop();
            _expired = true;
            _dialog.Hide();
        }
    }
}
