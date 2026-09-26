using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace LockscreenGif.Privileged.Helper;

internal static class ParentIdentity
{
    public static Process Verify(NamedPipeClientStream pipe, int expectedPid, string sid)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var actual) || actual != expectedPid)
        {
            throw new UnauthorizedAccessException("Unexpected pipe server.");
        }

        var parent = Process.GetProcessById(expectedPid);
        try
        {
            if (!OpenProcessToken(parent.Handle, 8, out var token))
            {
                throw new UnauthorizedAccessException("Cannot verify parent token.");
            }

            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            {
                if (identity.User?.Value != sid)
                {
                    throw new UnauthorizedAccessException("Parent SID mismatch.");
                }
            }

            var expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "LockscreenGif.exe"));
            if (!string.Equals(parent.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("Unexpected initiating executable.");
            }

            return parent;
        }
        catch
        {
            parent.Dispose();
            throw;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
}
