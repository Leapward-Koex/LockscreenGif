using System.Diagnostics;

namespace LockscreenGif.Services.Lockscreen;

internal static class PermissionWorkerCommand
{
    public static ProcessStartInfo Create(string root, string sid, string pipeName)
    {
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(AppContext.BaseDirectory, "Helpers", "Privileged", "LockscreenGif.Privileged.Helper.exe"),
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.Combine(AppContext.BaseDirectory, "Helpers", "Privileged"),
        };
        info.ArgumentList.Add(pipeName);
        info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.ArgumentList.Add(sid);
        return info;
    }
}
