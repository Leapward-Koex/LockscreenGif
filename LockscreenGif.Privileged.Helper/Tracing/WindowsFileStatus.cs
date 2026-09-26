using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LockscreenGif.Privileged.Helper.Tracing;

internal static class WindowsFileStatus
{
    public static string Describe(uint status)
    {
        var error = RtlNtStatusToDosError(status);
        return error == 317 ? $"NTSTATUS 0x{status:X8}" : new Win32Exception(unchecked((int)error)).Message;
    }

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(uint status);
}
