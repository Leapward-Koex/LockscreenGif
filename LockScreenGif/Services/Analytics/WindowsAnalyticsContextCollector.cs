using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace LockscreenGif.Services.Analytics;

internal static class WindowsAnalyticsContextCollector
{
    // Invoked only by the analytics worker after consent checks. No processes, WMI, network, or elevation prompts.
    public static AnalyticsWindowsContext Collect() => OperatingSystem.IsWindows() ? CollectWindows() : new();

    [SupportedOSPlatform("windows")]
    private static AnalyticsWindowsContext CollectWindows() =>
        new()
        {
            ProductSku = ReadValue(() => GetProductInfo(10, 0, 0, 0, out var sku) ? sku : (uint?)null),
            Release = ReadText(() => ReadVersionValue("DisplayVersion") as string),
            Build = ReadValue(() => (int?)Environment.OSVersion.Version.Build),
            UpdateRevision = ReadValue(() => ReadVersionValue("UBR") as int?),
            OsArchitecture = ReadValue(() => (Architecture?)RuntimeInformation.OSArchitecture),
            ProcessArchitecture = ReadValue(() => (Architecture?)RuntimeInformation.ProcessArchitecture),
            UserLocale = ReadText(() => ReadLocale(GetUserDefaultLocaleName)),
            SystemLocale = ReadText(() => ReadLocale(GetSystemDefaultLocaleName)),
            DisplayLanguage = ReadText(ReadDisplayLanguage),
            InstallLanguage = ReadText(() => CultureInfo.GetCultureInfo(GetSystemDefaultUILanguage()).Name),
            IsElevated = ReadValue(() =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                return (bool?)new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }),
            IsRemoteSession = ReadValue(() => (bool?)(GetSystemMetrics(0x1000) != 0)),
        };

    [SupportedOSPlatform("windows")]
    private static object? ReadVersionValue(string name)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var version = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return version?.GetValue(name);
    }

    private static string? ReadLocale(Func<StringBuilder, int, int> read)
    {
        var buffer = new StringBuilder(85); // LOCALE_NAME_MAX_LENGTH, including terminator.
        return read(buffer, buffer.Capacity) > 0 ? buffer.ToString() : null;
    }

    private static string? ReadDisplayLanguage()
    {
        const uint muiLanguageName = 0x8;
        uint length = 0;
        if (!GetUserPreferredUILanguages(muiLanguageName, out _, null, ref length) || length is < 2 or > 4096)
        {
            return null;
        }

        var buffer = new char[length];
        if (!GetUserPreferredUILanguages(muiLanguageName, out var count, buffer, ref length) || count == 0)
        {
            return null;
        }

        var end = Array.IndexOf(buffer, '\0');
        // Only the first preferred display language, never the full language list.
        return end > 0 ? new string(buffer, 0, end) : null;
    }

    private static T? ReadValue<T>(Func<T?> read)
        where T : struct
    {
        try
        {
            return read();
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadText(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProductInfo(uint major, uint minor, uint servicePackMajor, uint servicePackMinor, out uint sku);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetUserDefaultLocaleName(StringBuilder name, int count);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetSystemDefaultLocaleName(StringBuilder name, int count);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserPreferredUILanguages(uint flags, out uint count, [Out] char[]? languages, ref uint length);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern ushort GetSystemDefaultUILanguage();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetSystemMetrics(int index);
}
