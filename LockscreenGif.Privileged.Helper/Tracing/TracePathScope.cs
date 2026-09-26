using System.Runtime.InteropServices;
using System.Text;
using LockscreenGif.Privileged;

namespace LockscreenGif.Privileged.Helper.Tracing;

internal sealed class TracePathScope
{
    private readonly string _root;
    private readonly string _source;
    private readonly List<(string Device, string Drive)> _volumes = [];

    public TracePathScope(TraceScope scope)
    {
        _root = Path.GetFullPath(scope.CacheDirectory).TrimEnd('\\') + "\\";
        _source = Path.GetFullPath(scope.SourcePath);
        foreach (var drive in Environment.GetLogicalDrives())
        {
            var buffer = new StringBuilder(1024);
            if (QueryDosDevice(drive[..2], buffer, buffer.Capacity) != 0)
            {
                _volumes.Add((buffer.ToString(), drive[..2]));
            }
        }
        _volumes.Sort((a, b) => b.Device.Length.CompareTo(a.Device.Length));
    }

    public string? Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096)
        {
            return null;
        }

        if (path.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            path = path[4..];
        }

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            path = path[4..];
        }

        if (path.StartsWith(@"\Device\Mup\", StringComparison.OrdinalIgnoreCase))
        {
            path = @"\\" + path[12..];
        }

        foreach (var (device, drive) in _volumes)
        {
            if (path.StartsWith(device + "\\", StringComparison.OrdinalIgnoreCase))
            {
                path = drive + path[device.Length..];
                break;
            }
        }

        try
        {
            return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public bool Contains(string path) =>
        path.StartsWith(_root, StringComparison.OrdinalIgnoreCase) || path.Equals(_source, StringComparison.OrdinalIgnoreCase);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string device, StringBuilder target, int size);
}
