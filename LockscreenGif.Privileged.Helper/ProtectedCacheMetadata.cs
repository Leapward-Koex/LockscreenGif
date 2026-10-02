using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace LockscreenGif.Privileged.Helper;

/// <summary>Uses a temporary backup-enabled thread token only to inspect denied cache metadata.</summary>
internal static class ProtectedCacheMetadata
{
    public static FileAttributes Read(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (UnauthorizedAccessException)
        {
            // Elevation alone does not bypass a SYSTEM-only DACL. Duplicate the
            // current token so enabling backup access never changes the process
            // token, other threads, or the token inherited by permission tools.
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate);
            const uint tokenAccess = 0x0008 | 0x0004 | 0x0020; // QUERY | IMPERSONATE | ADJUST_PRIVILEGES
            if (!DuplicateTokenEx(identity.AccessToken, tokenAccess, IntPtr.Zero, 2, 2, out var token))
            {
                throw Failure(Marshal.GetLastWin32Error());
            }
            using (token)
            {
                if (!LookupPrivilegeValueW(null, "SeBackupPrivilege", out var luid))
                {
                    throw Failure(Marshal.GetLastWin32Error());
                }
                var privileges = new TokenPrivileges
                {
                    Count = 1,
                    Luid = luid,
                    Attributes = 2,
                };
                if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
                {
                    throw Failure(Marshal.GetLastWin32Error());
                }
                var error = Marshal.GetLastWin32Error();
                if (error != 0) // AdjustTokenPrivileges may succeed with ERROR_NOT_ALL_ASSIGNED.
                {
                    throw Failure(error);
                }
                return WindowsIdentity.RunImpersonated(token, () => ReadFromHandle(path));
            }
        }
    }

    private static FileAttributes ReadFromHandle(string path)
    {
        const uint readAttributes = 0x80;
        const uint openExisting = 3;
        const uint backupSemantics = 0x02000000;
        const uint openReparsePoint = 0x00200000;
        using var handle = CreateFileW(
            path,
            readAttributes,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            openExisting,
            backupSemantics | openReparsePoint,
            IntPtr.Zero
        );
        if (handle.IsInvalid)
        {
            throw MetadataFailure(path, Marshal.GetLastWin32Error());
        }
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw MetadataFailure(path, Marshal.GetLastWin32Error());
        }
        return information.Attributes;
    }

    private static Exception MetadataFailure(string path, int code)
    {
        var native = new Win32Exception(code);
        var message = $"Could not inspect cache path '{path}': {native.Message}";
        return code switch
        {
            2 => new FileNotFoundException(message, path),
            3 => new DirectoryNotFoundException(message),
            5 => new UnauthorizedAccessException(message, native),
            _ => new IOException(message, unchecked((int)(0x80070000u | (uint)code))),
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public FileAttributes Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string path,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile
    );

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    private static UnauthorizedAccessException Failure(int error) =>
        new("The permission helper could not enable protected cache metadata inspection.", new Win32Exception(error));

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint Count;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        SafeAccessTokenHandle existingToken,
        uint desiredAccess,
        IntPtr attributes,
        int impersonationLevel,
        int tokenType,
        out SafeAccessTokenHandle newToken
    );

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        SafeAccessTokenHandle token,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength
    );
}
