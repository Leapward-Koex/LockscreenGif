using System.Diagnostics;
using System.Runtime.InteropServices;
using WinUIEx.Messaging;

namespace LockscreenGif.Services.Diagnostics;

/// <summary>Owns native registrations; publishes observations on the window's thread.</summary>
public sealed class WindowsSessionMonitor : IDisposable
{
    private static readonly Guid DisplayStateSetting = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    private readonly int _sessionId = GetSessionId();
    private WindowMessageMonitor? _messages;
    private IntPtr _window;
    private IntPtr _powerRegistration;
    public bool IsRegistered { get; private set; }
    public int SessionId => _sessionId;
    public string? Error { get; private set; }
    public bool PowerNotificationsAvailable { get; private set; }
    public string? PowerError { get; private set; }
    public event Action<string>? Observed;

    public void Start(IntPtr window)
    {
        if (_messages is not null && _window != window)
        {
            Dispose();
        }

        if (_messages is not null && IsRegistered && PowerNotificationsAvailable)
        {
            return;
        }

        try
        {
            _window = window;
            if (_messages is null)
            {
                _messages = new WindowMessageMonitor(window);
                _messages.WindowMessageReceived += OnMessage;
            }
            if (!IsRegistered)
            {
                Error = null;
                IsRegistered = WTSRegisterSessionNotification(window, 0);
                if (!IsRegistered)
                {
                    Error = $"Session notifications unavailable (Win32 {Marshal.GetLastWin32Error()}).";
                }
            }
            if (!PowerNotificationsAvailable)
            {
                PowerError = null;
                var displayState = DisplayStateSetting;
                _powerRegistration = RegisterPowerSettingNotification(window, ref displayState, 0);
                PowerNotificationsAvailable = _powerRegistration != IntPtr.Zero;
                if (!PowerNotificationsAvailable)
                {
                    PowerError = $"Display-power notifications unavailable (Win32 {Marshal.GetLastWin32Error()}).";
                    Logger.Warn(PowerError);
                }
            }
        }
        catch (Exception ex)
        {
            Dispose();
            Error = $"Windows monitoring unavailable: {ex.GetType().Name} (0x{ex.HResult:X8}).";
            PowerError = Error;
            Logger.Error(Error, ex);
        }
    }

    private static int GetSessionId()
    {
        using var process = Process.GetCurrentProcess();
        return process.SessionId;
    }

    private void OnMessage(object? sender, WindowMessageEventArgs e)
    {
        var code = unchecked((int)e.Message.WParam.ToUInt64());
        if (e.Message.MessageId == 0x02B1 && e.Message.LParam.ToInt64() == _sessionId)
        {
            var name = code switch
            {
                1 => "ConsoleConnected",
                2 => "ConsoleDisconnected",
                3 => "RemoteConnected",
                4 => "RemoteDisconnected",
                6 => "SessionLogoff",
                7 => "SessionLock",
                8 => "SessionUnlock",
                _ => null,
            };
            if (name is not null)
            {
                Observed?.Invoke(name);
            }
        }
        else if (e.Message.MessageId == 0x007E)
        {
            Observed?.Invoke("DisplayConfigurationChanged");
        }
        else if (e.Message.MessageId == 0x0218)
        {
            if (code == 4)
            {
                Observed?.Invoke("SystemSuspend");
            }
            else if (code is 7 or 18)
            {
                Observed?.Invoke("SystemResume");
            }
            else if (code == 0x8013 && e.Message.LParam != IntPtr.Zero)
            {
                // POWERBROADCAST_SETTING: GUID, DWORD length, DWORD display state.
                if (Marshal.PtrToStructure<Guid>(e.Message.LParam) != DisplayStateSetting)
                {
                    return;
                }

                var length = Marshal.ReadInt32(e.Message.LParam, 16);
                if (length == 4)
                {
                    Observed?.Invoke(
                        Marshal.ReadInt32(e.Message.LParam, 20) switch
                        {
                            0 => "DisplayOff",
                            1 => "DisplayOn",
                            2 => "DisplayDimmed",
                            _ => "DisplayPowerChanged",
                        }
                    );
                }
            }
        }
    }

    public bool TryLock(out string? error)
    {
        var requested = LockWorkStation();
        error = requested ? null : $"Windows could not request locking (Win32 {Marshal.GetLastWin32Error()}).";
        return requested;
    }

    public void Dispose()
    {
        if (IsRegistered)
        {
            WTSUnRegisterSessionNotification(_window);
        }

        if (_powerRegistration != IntPtr.Zero)
        {
            UnregisterPowerSettingNotification(_powerRegistration);
        }

        if (_messages is not null)
        {
            _messages.WindowMessageReceived -= OnMessage;
            _messages.Dispose();
        }
        _messages = null;
        _powerRegistration = IntPtr.Zero;
        IsRegistered = false;
        PowerNotificationsAvailable = false;
        Error = null;
        PowerError = null;
        _window = IntPtr.Zero;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSRegisterSessionNotification(IntPtr window, uint flags);

    [DllImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
}
