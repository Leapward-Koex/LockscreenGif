using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LockscreenGif.Services.Lockscreen;

/// <summary>Distinguishes file sharing restrictions from denied DELETE access before UAC.</summary>
internal static class CacheDeleteAccess
{
    public static bool IsDenied(string path)
    {
        const uint deleteAccess = 0x00010000;
        const uint shareReadWriteDelete = 0x00000007;
        const uint openExisting = 3;
        using var handle = CreateFile(path, deleteAccess, shareReadWriteDelete, IntPtr.Zero, openExisting, 0, IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return false;
        }

        var error = Marshal.GetLastWin32Error();
        if (error is 32 or 33)
        {
            throw new IOException(
                "The cache destination is in use. Permission changes cannot release another process's file handle.",
                unchecked((int)(0x80070000u | (uint)error))
            );
        }
        // Requesting DELETE explicitly tests the access required for replacement. Other
        // failures (missing file, network/device errors) are not evidence for elevation.
        return error == 5;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile
    );
}
