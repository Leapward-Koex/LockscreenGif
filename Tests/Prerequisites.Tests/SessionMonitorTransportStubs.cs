namespace WinUIEx.Messaging;

public sealed class WindowMessageMonitor : IDisposable
{
    public WindowMessageMonitor(IntPtr window) =>
        throw new InvalidOperationException("Native window monitoring must not start in isolated message tests.");

    public event EventHandler<WindowMessageEventArgs>? WindowMessageReceived
    {
        add { }
        remove { }
    }

    public void Dispose() { }
}

public sealed class WindowMessageEventArgs : EventArgs
{
    public WindowMessage Message { get; set; }
}

public struct WindowMessage
{
    public uint MessageId { get; set; }
    public UIntPtr WParam { get; set; }
    public IntPtr LParam { get; set; }
}
