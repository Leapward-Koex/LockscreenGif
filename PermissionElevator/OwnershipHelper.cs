using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.AccessControl;
using System.ComponentModel;
using System.IO;

public class OwnershipHelper
{
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(
        IntPtr ProcessHandle,
        UInt32 DesiredAccess,
        out IntPtr TokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern bool LookupPrivilegeValue(
        string lpSystemName,
        string lpName,
        out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(
        IntPtr TokenHandle,
        bool DisableAllPrivileges,
        ref TOKEN_PRIVILEGES NewState,
        UInt32 BufferLength,
        IntPtr PreviousState,
        IntPtr ReturnLength);

    const UInt32 TOKEN_ADJUST_PRIVILEGES = 0x0020;
    const UInt32 TOKEN_QUERY = 0x0008;
    const UInt32 SE_PRIVILEGE_ENABLED = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    struct LUID
    {
        public UInt32 LowPart;
        public Int32 HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TOKEN_PRIVILEGES
    {
        public UInt32 PrivilegeCount;
        public LUID Luid;
        public UInt32 Attributes;
    }

    public static void EnablePrivilege(string privilege)
    {
        IntPtr tokenHandle;
        if (!OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle,
            TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out tokenHandle))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        LUID luid;
        if (!LookupPrivilegeValue(null, privilege, out luid))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES
        {
            PrivilegeCount = 1,
            Luid = luid,
            Attributes = SE_PRIVILEGE_ENABLED
        };

        if (!AdjustTokenPrivileges(tokenHandle, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        Marshal.FreeHGlobal(tokenHandle);
    }
}