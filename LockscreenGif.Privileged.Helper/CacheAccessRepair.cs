using System.Diagnostics;
using System.Security.Principal;

namespace LockscreenGif.Privileged.Helper;

internal sealed class CacheAccessRepair
{
    private readonly string _sid;
    private readonly Func<string, string[], Task<int>> _run;
    public string Root { get; }

    public CacheAccessRepair(
        string sid,
        string? fixtureRoot = null,
        Func<string, string[], Task<int>>? run = null,
        CancellationToken lifetime = default
    )
    {
        _sid = new SecurityIdentifier(sid).Value;
        _run = run ?? ((name, args) => ToolAsync(name, args, lifetime));
        Root =
            fixtureRoot
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Microsoft",
                "Windows",
                "SystemData",
                _sid,
                "ReadOnly"
            );
    }

    internal void Validate(string path, bool write)
    {
        var full = Path.GetFullPath(path);
        if (full.Equals(Root, StringComparison.OrdinalIgnoreCase))
        {
            if (write)
            {
                throw new UnauthorizedAccessException("The cache root only accepts read access.");
            }
        }
        else
        {
            if (!full.StartsWith(Root + "\\", StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("Outside the current user's cache.");
            }

            var parts = full[(Root.Length + 1)..].Split('\\');
            if (
                parts.Length > 2
                || !parts[0].StartsWith("LockScreen", StringComparison.OrdinalIgnoreCase)
                || (
                    parts.Length == 2
                    && !parts[1].Equals("LockScreen.jpg", StringComparison.OrdinalIgnoreCase)
                    && !parts[1].EndsWith("_notdimmed.jpg", StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                throw new UnauthorizedAccessException("Unexpected cache path.");
            }
        }
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("Permission repair does not follow links.");
            }
        }
    }

    public async Task<int> GrantAsync(string path, bool write)
    {
        Validate(path, write);
        var grant = "*" + _sid + ":" + (write ? "M" : "RX");
        var result = await _run("icacls.exe", [path, "/grant", grant]);
        if (result == 0)
        {
            return 0;
        }

        Validate(path, write);
        result = await _run("takeown.exe", ["/f", path, "/a"]);
        if (result != 0)
        {
            return result;
        }

        Validate(path, write);
        return await _run("icacls.exe", [path, "/grant", grant]);
    }

    private static async Task<int> ToolAsync(string name, string[] args, CancellationToken lifetime)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, name))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new IOException("Access tool did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(lifetime);
        }
        catch (OperationCanceledException)
        {
            // Parent loss and the watchdog also bound a native access tool that hangs.
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            await Task.WhenAll(output, error);
            throw;
        }
        await Task.WhenAll(output, error);
        return process.ExitCode;
    }
}
