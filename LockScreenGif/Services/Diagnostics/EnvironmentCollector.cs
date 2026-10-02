using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using LockscreenGif.Privileged;
using LockscreenGif.Services.Lockscreen;
using Microsoft.Win32;

namespace LockscreenGif.Services.Diagnostics;

/// <summary>Read-only configuration evidence. No ownership, permission, or personalization changes.</summary>
public static class EnvironmentCollector
{
    public static Dictionary<string, string> Collect(string cacheDirectory)
    {
        var data = new Dictionary<string, string>();
        void Capture(string name, Func<string> read)
        {
            try
            {
                data[name] = read();
            }
            catch (Exception ex)
            {
                data[name] = $"Unavailable ({ex.GetType().Name}: {ex.Message})";
            }
        }
        Capture(
            "App version",
            () =>
                Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? "Unknown"
        );
        Capture("App build ID", () => Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId.ToString());
        Capture(
            "Original lock-screen image (may be unavailable)",
            () => Windows.System.UserProfile.LockScreen.OriginalImageFile?.ToString() ?? "Not available"
        );
        Capture("Operating system", () => RuntimeInformation.OSDescription);
        Capture(
            "OS build",
            () =>
                ReadRegistry(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber")
                + "."
                + ReadRegistry(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR")
        );
        Capture("OS edition", () => ReadRegistry(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID"));
        Capture("OS release", () => ReadRegistry(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion"));
        data["OS architecture"] = RuntimeInformation.OSArchitecture.ToString();
        data["Process architecture"] = RuntimeInformation.ProcessArchitecture.ToString();
        Capture("Package identity", () => Windows.ApplicationModel.Package.Current.Id.FullName);
        Capture(
            "Elevated administrator",
            () => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator).ToString()
        );
        Capture("Session ID", () => Process.GetCurrentProcess().SessionId.ToString());
        Capture("Remote session", () => (GetSystemMetrics(0x1000) != 0).ToString());
        Capture("Animation effects", () => new Windows.UI.ViewManagement.UISettings().AnimationsEnabled.ToString());
        Capture(
            DiagnosticWindowsImageFeature.EnvironmentKey,
            () => WindowsImageFeature.Describe(WindowsImageFeatureSettings.Current.Read())
        );
        Capture("Energy saver", () => Windows.System.Power.PowerManager.EnergySaverStatus.ToString());
        Capture("Power supply", () => Windows.System.Power.PowerManager.PowerSupplyStatus.ToString());
        Capture("Lock screen mode (heuristic)", ReadMode);
        Capture("Display configuration", ReadDisplays);
        const string policyPath = @"SOFTWARE\Policies\Microsoft\Windows\Personalization";
        foreach (var hive in new[] { (Name: "Machine", Key: Registry.LocalMachine), (Name: "User", Key: Registry.CurrentUser) })
        {
            foreach (var value in new[] { "NoChangingLockScreen", "LockScreenImage", "NoLockScreen", "NoLockScreenSlideshow" })
            {
                Capture($"{hive.Name} policy / {value}", () => ReadRegistry(hive.Key, policyPath, value));
            }
        }

        Capture(
            "MDM lock screen image status",
            () =>
                ReadRegistry(
                    Registry.LocalMachine,
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP",
                    "LockScreenImageStatus"
                )
        );
        data["Policy interpretation"] = "Presence is configuration evidence, not proof a policy caused this result.";
        data["Cache directory"] = cacheDirectory;
        Capture(
            "Cache enumeration",
            () =>
            {
                if (!Directory.Exists(cacheDirectory))
                {
                    return "Directory absent or inaccessible; presence cannot be confirmed.";
                }

                return $"Readable; first {Directory.EnumerateFileSystemEntries(cacheDirectory).Take(100).Count()} entries enumerated (limit 100).";
            }
        );
        Capture(
            "Cache read capability",
            () =>
            {
                var first = Directory.EnumerateFiles(cacheDirectory, "LockScreen*", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (first is null)
                {
                    return "Unknown: no immediate cache file available to test. Session snapshots test nested files.";
                }

                using var stream = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                stream.ReadByte();
                return "One immediate file readable; session snapshots report per-file access.";
            }
        );
        data["Cache write capability"] = "Not tested during read-only inspection; actual apply operations report their outcomes.";
        return data;
    }

    private static string ReadRegistry(RegistryKey hive, string path, string name)
    {
        using var key = hive.OpenSubKey(path, writable: false);
        return key?.GetValue(name)?.ToString() ?? "Not present";
    }

    private static string ReadMode()
    {
        using var creative = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lock Screen\Creative");
        if (
            !string.IsNullOrWhiteSpace(creative?.GetValue("CreativeId") as string)
            || !string.IsNullOrWhiteSpace(creative?.GetValue("CreativeJson") as string)
        )
        {
            return "Spotlight suggested by Creative registry values; these may be stale.";
        }

        using var slideshow = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lock Screen");
        if (slideshow?.GetValue("SlideshowEnabled") is int enabled && enabled != 0)
        {
            return "Slideshow suggested by registry values; these may be stale.";
        }

        return "Unknown. No affirmative Spotlight or slideshow evidence; Picture mode is not confirmed.";
    }

    private static string ReadDisplays()
    {
        var displays = new List<string>();
        if (
            !EnumDisplayMonitors(
                IntPtr.Zero,
                IntPtr.Zero,
                (IntPtr monitor, IntPtr dc, ref Rect bounds, IntPtr user) =>
                {
                    var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                    if (!GetMonitorInfo(monitor, ref info))
                    {
                        return true;
                    }

                    var width = info.Monitor.Right - info.Monitor.Left;
                    var height = info.Monitor.Bottom - info.Monitor.Top;
                    var dpiStatus = GetDpiForMonitor(monitor, 0, out var dpiX, out var dpiY);
                    displays.Add(
                        $"{width}x{height}, {(width >= height ? "landscape" : "portrait")}, "
                            + $"primary={(info.Flags & 1) != 0}, scale={(dpiStatus == 0 ? $"{dpiX / 96d:P0} ({dpiX}x{dpiY} DPI)" : "unknown")}"
                    );
                    return true;
                },
                IntPtr.Zero
            )
        )
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        return displays.Count == 0 ? "Unknown: no displays enumerated." : string.Join("; ", displays);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left,
            Top,
            Right,
            Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor,
            Work;
        public uint Flags;
    }

    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref Rect bounds, IntPtr data);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
