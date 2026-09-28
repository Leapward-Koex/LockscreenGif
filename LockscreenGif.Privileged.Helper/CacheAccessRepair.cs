using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace LockscreenGif.Privileged.Helper;

internal sealed class CacheAccessRepair
{
    private readonly string _sid;
    private readonly Func<string, string[], Task<int>> _run;
    private readonly Func<string, FileAttributes> _readAttributes;
    private readonly string _metadataParent;
    private readonly Action _grantParentMetadata;
    public string Root { get; }

    public CacheAccessRepair(
        string sid,
        string? fixtureRoot = null,
        Func<string, string[], Task<int>>? run = null,
        CancellationToken lifetime = default,
        Func<string, FileAttributes>? readAttributes = null,
        Action? grantParentMetadata = null
    )
    {
        _sid = new SecurityIdentifier(sid).Value;
        _run = run ?? ((name, args) => ToolAsync(name, args, lifetime));
        _readAttributes = readAttributes ?? ProtectedCacheMetadata.Read;
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
        Root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar);
        _metadataParent = Path.GetDirectoryName(Root) ?? throw new ArgumentException("The cache must have a user parent directory.");
        _grantParentMetadata = grantParentMetadata ?? GrantParentMetadata;
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
            if ((_readAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("Permission repair does not follow links.");
            }
        }
    }

    public async Task<int> GrantAsync(string path, bool write)
    {
        Validate(path, write);
        // The client must still reject links after repair. Permit metadata reads
        // on the fixed SID parent, without granting listing, file data,
        // write access, inheritance, or any access to the SystemData parent.
        try
        {
            _grantParentMetadata();
        }
        catch (UnauthorizedAccessException)
        {
            Validate(path, write);
            var result = await _run("takeown.exe", ["/f", _metadataParent, "/a"]);
            if (result != 0)
            {
                return result;
            }
            Validate(path, write);
            _grantParentMetadata();
        }
        return await RepairPathAsync(path, write ? "M" : "RX", () => Validate(path, write));
    }

    internal void GrantParentMetadata()
    {
        // icacls may itself require readable attributes before applying a grant,
        // even after takeown succeeded. The ACL API uses the owner's READ_CONTROL
        // and WRITE_DAC rights directly, preserving other entries and inheritance.
        var parent = new DirectoryInfo(_metadataParent);
        var acl = parent.GetAccessControl(AccessControlSections.Access);
        acl.AddAccessRule(new(new SecurityIdentifier(_sid), FileSystemRights.ReadAttributes, AccessControlType.Allow));
        parent.SetAccessControl(acl);
    }

    private async Task<int> RepairPathAsync(string path, string rights, Action validate)
    {
        validate();
        var grant = "*" + _sid + ":" + rights;
        var result = await _run("icacls.exe", [path, "/grant", grant]);
        if (result == 0)
        {
            return 0;
        }

        validate();
        result = await _run("takeown.exe", ["/f", path, "/a"]);
        if (result != 0)
        {
            return result;
        }

        validate();
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
