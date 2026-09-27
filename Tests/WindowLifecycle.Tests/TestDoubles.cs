namespace Microsoft.UI.Xaml
{
    public class RoutedEventArgs : EventArgs { }

    public class WindowEventArgs : EventArgs { }

    public class UIElement
    {
        public bool IsHitTestVisible { get; set; } = true;
    }
}

namespace Microsoft.UI.Xaml.Controls
{
    public class Control : Microsoft.UI.Xaml.UIElement
    {
        public bool IsEnabled { get; set; } = true;
        public object? Content { get; set; }
    }
}

namespace Microsoft.UI.Windowing
{
    public sealed class AppWindowClosingEventArgs : EventArgs
    {
        public bool Cancel { get; set; }
    }

    public sealed class AppWindow
    {
        public event Action<AppWindow, AppWindowClosingEventArgs>? Closing;
        public bool InClosingCallback { get; private set; }

        public bool RequestClose()
        {
            var args = new AppWindowClosingEventArgs();
            InClosingCallback = true;
            try
            {
                Closing?.Invoke(this, args);
            }
            finally
            {
                InClosingCallback = false;
            }
            return args.Cancel;
        }
    }
}

namespace LockscreenGif.Contracts.Services
{
    public interface ILockscreenService
    {
        Task WaitForIdleAsync();
    }
}

namespace LockscreenGif.Services.Diagnostics
{
    public sealed class DiagnosticsSessionService
    {
        public bool IsRunning { get; set; }
        public Task Completion { get; set; } = Task.CompletedTask;
        public int CloseCalls { get; private set; }
        public bool Interrupted { get; private set; }

        public Task CloseAsync(string reason)
        {
            CloseCalls++;
            return Completion;
        }

        public void Interrupt(string reason) => Interrupted = true;
    }

    public sealed class LockscreenVerificationService
    {
        public Task Completion { get; set; } = Task.CompletedTask;
        public int CloseCalls { get; private set; }

        public Task CloseAsync()
        {
            CloseCalls++;
            return Completion;
        }
    }

    public sealed class WindowsSessionMonitor
    {
        public int DisposeCalls { get; private set; }

        public void Dispose()
        {
            if (App.MainWindow.AppWindow.InClosingCallback)
            {
                throw new InvalidOperationException("Message hooks removed inside the native close callback.");
            }
            DisposeCalls++;
        }
    }
}

namespace WindowLifecycle.Tests
{
    public sealed class FakeDispatcher
    {
        private readonly Queue<Action> _queue = new();
        public bool AcceptWork { get; set; } = true;
        public int Count => _queue.Count;

        public bool TryEnqueue(Action callback)
        {
            if (!AcceptWork)
            {
                return false;
            }
            _queue.Enqueue(callback);
            return true;
        }

        public void Pump() => _queue.Dequeue()();
    }

    public sealed class FakeWindow
    {
        public Microsoft.UI.Windowing.AppWindow AppWindow { get; } = new();
        public FakeDispatcher DispatcherQueue { get; } = new();
        public object? Content { get; set; } = new LockscreenGif.Views.ShellPage();
        public event Action<object, Microsoft.UI.Xaml.WindowEventArgs>? Closed;
        public TaskCompletionSource CloseCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CloseCalls { get; private set; }

        public void Close()
        {
            if (AppWindow.InClosingCallback)
            {
                throw new InvalidOperationException("Reentrant native window close.");
            }
            CloseCalls++;
            Closed?.Invoke(this, new());
            CloseCompleted.TrySetResult();
        }
    }

    public sealed class FakeLockscreenService : LockscreenGif.Contracts.Services.ILockscreenService
    {
        public Task Idle { get; set; } = Task.CompletedTask;

        public Task WaitForIdleAsync() => Idle;
    }

    public sealed class FakeSession
    {
        public event Action<object, object>? SeekCompleted;
        public int Subscribers => SeekCompleted?.GetInvocationList().Length ?? 0;
    }

    public sealed class FakePlayer : IDisposable
    {
        public event Action<object, object>? MediaEnded;
        public event Action<object, object>? MediaFailed;
        public int Subscribers => (MediaEnded?.GetInvocationList().Length ?? 0) + (MediaFailed?.GetInvocationList().Length ?? 0);
        public FakeSession Session { get; } = new();
        public int PauseCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public Action? BeforeDispose { get; set; }

        public void Pause()
        {
            ObjectDisposedException.ThrowIf(DisposeCalls != 0, this);
            PauseCalls++;
        }

        public void Dispose()
        {
            BeforeDispose?.Invoke();
            DisposeCalls++;
        }
    }

    public sealed class FakePreview
    {
        public FakePlayer? MediaPlayer { get; private set; }

        public void SetMediaPlayer(FakePlayer? player) => MediaPlayer = player;
    }
}

namespace LockscreenGif
{
    public partial class App
    {
        private static readonly Dictionary<Type, object> Services = new();
        public static WindowLifecycle.Tests.FakeWindow MainWindow { get; private set; } = new();
        public static int AnalyticsStops { get; private set; }

        public App()
        {
            MainWindow = new();
            AnalyticsStops = 0;
            Services.Clear();
            MainWindow.AppWindow.Closing += MainWindow_Closing;
        }

        public static void Register<T>(T service)
            where T : class => Services[typeof(T)] = service;

        public static T GetService<T>()
            where T : class => (T)Services[typeof(T)];

        private static void StopAnalytics() => AnalyticsStops++;
    }

    internal static class Logger
    {
        public static void Error(string message, Exception exception) { }
    }
}

namespace LockscreenGif.Views
{
    public sealed class ShellPage : Microsoft.UI.Xaml.Controls.Control
    {
        public int FeedbackStops { get; private set; }

        public void StopLockscreenFeedback() => FeedbackStops++;
    }

    public sealed partial class MainPage
    {
        private bool _playing = true,
            _mediaReady,
            _playbackSeekPending = true,
            _resumeAfterInteraction = true,
            _interacting = true;
        private WindowLifecycle.Tests.FakeSession? _session;
        private readonly CancellationTokenSource _videoLoadCts = new(),
            _previewCts = new(),
            _editorCts = new();
        public WindowLifecycle.Tests.FakePreview VideoPreview { get; } = new();
        private readonly Microsoft.UI.Xaml.Controls.Control PlaySelectionButton = new();
        private readonly Microsoft.UI.Xaml.Controls.Control SelectionStartButton = new();
        private readonly Microsoft.UI.Xaml.Controls.Control SelectionEndButton = new();
        public bool Rendering { get; private set; } = true;
        public bool Polling { get; private set; } = true;
        public bool PreviewLoading { get; private set; } = true;
        public bool HasSession => _session is not null;
        public bool MediaReady => _mediaReady;
        public bool PlaybackStopped => !_playing && !Rendering && !_playbackSeekPending;
        public bool Suspended => PlaybackStopped && !_resumeAfterInteraction && !_interacting && !PreviewLoading;
        public bool WorkCancelled =>
            _videoLoadCts.IsCancellationRequested && _previewCts.IsCancellationRequested && _editorCts.IsCancellationRequested;

        public MainPage(WindowLifecycle.Tests.FakePlayer? player)
        {
            App.MainWindow.Closed += MainWindow_Closed;
            if (player is not null)
            {
                VideoPreview.SetMediaPlayer(player);
                _session = player.Session;
                _session.SeekCompleted += Session_SeekCompleted;
                player.MediaEnded += VideoPreview_MediaEnded;
                player.MediaFailed += VideoPreview_MediaFailed;
                _mediaReady = true;
            }
        }

        public void Unload() => MainPage_Unloaded(this, new());

        public void Release() => ReleasePreviewPlayer();

        private void StopLockscreenModePolling() => Polling = false;

        private void StopPlaybackRendering() => Rendering = false;

        private void SetPreviewLoading(bool loading) => PreviewLoading = loading;

        private void Session_SeekCompleted(object sender, object args) { }

        private void VideoPreview_MediaEnded(object sender, object args) { }

        private void VideoPreview_MediaFailed(object sender, object args) { }
    }
}
