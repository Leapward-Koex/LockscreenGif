using System.Diagnostics;
using System.Security.Principal;
using LockscreenGif.Privileged.Helper;
using LockscreenGif.Tests;

namespace ProcessTracing.Tests;

internal static class NativePermissionTests
{
    public static async Task RunAsync()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new InvalidOperationException("Run the native permission fixture as administrator.");
        }
        using var fixture = new ProtectedCacheFixture();
        ExpectDenied(() => File.GetAttributes(fixture.Parent));
        // Warm up the test harness's process APIs before taking the baseline:
        // their first process inspection can enable the process debug privilege.
        await RunTool("whoami.exe", ["/priv"]);
        var tokenBefore = await RunTool("whoami.exe", ["/priv"]);
        Program.Check(
            (ProtectedCacheMetadata.Read(fixture.Parent) & FileAttributes.Directory) != 0,
            "Backup-enabled metadata read inspects a genuinely protected SID fixture"
        );
        Program.Check(
            (ProtectedCacheMetadata.Read(fixture.Link) & FileAttributes.ReparsePoint) != 0,
            "Backup-enabled metadata read sees the protected link itself"
        );
        var tokenAfter = await RunTool("whoami.exe", ["/priv"]);
        CheckToken(tokenBefore, tokenAfter);

        var forcedFirstDenial = false;
        var ownershipCalls = 0;
        var toolEvidence = new List<string>();
        var deniedMetadata = false;
        CacheAccessRepair? repair = null;
        repair = new CacheAccessRepair(
            identity.User!.Value,
            fixture.Root,
            async (name, args) =>
            {
                // Exercise the actual native ownership fallback deterministically,
                // even though the creator owns these private fixture objects.
                if (!forcedFirstDenial && name == "icacls.exe")
                {
                    forcedFirstDenial = true;
                    return 5;
                }
                if (name == "takeown.exe")
                {
                    ownershipCalls++;
                }
                var result = await RunTool(name, args);
                toolEvidence.Add($"{name} exit={result.Code}: {result.Output}");
                return result.Code;
            },
            grantParentMetadata: () =>
            {
                if (!deniedMetadata)
                {
                    deniedMetadata = true;
                    throw new UnauthorizedAccessException("Synthetic initial metadata ACL denial.");
                }
                repair!.GrantParentMetadata();
            }
        );
        var rootResult = await repair.GrantAsync(fixture.Root, false);
        if (rootResult != 0 || ownershipCalls != 2)
        {
            throw new InvalidOperationException(
                $"Native root repair returned {rootResult}, ownership calls={ownershipCalls}: {string.Join('\n', toolEvidence)}"
            );
        }
        Program.Check(
            (File.GetAttributes(fixture.Parent) & FileAttributes.Directory) != 0,
            "Ordinary metadata reads succeed after the ReadAttributes grant"
        );
        ExpectDenied(() => Directory.GetDirectories(fixture.Parent));
        Program.Check(
            await repair.GrantAsync(fixture.Folder, true) == 0 && await repair.GrantAsync(fixture.Image, true) == 0,
            "Native scoped grants restore folder and image writes"
        );
        await File.WriteAllTextAsync(fixture.Image, "native scoped repair verified");
        Program.Check(
            await File.ReadAllTextAsync(fixture.Image) == "native scoped repair verified",
            "Fixture bytes can be written and read after native recovery"
        );
        CheckToken(tokenBefore, await RunTool("whoami.exe", ["/priv"]));
    }

    private static void CheckToken((int Code, string Output) before, (int Code, string Output) after)
    {
        var beforeLines = before
            .Output.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("Se", StringComparison.Ordinal))
            .Order()
            .ToArray();
        var afterLines = after
            .Output.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("Se", StringComparison.Ordinal))
            .Order()
            .ToArray();
        if (before.Code != 0 || after.Code != 0 || beforeLines.Length == 0 || !beforeLines.SequenceEqual(afterLines))
        {
            throw new InvalidOperationException(
                $"Privilege inspection changed. Before exit={before.Code}: {before.Output} After exit={after.Code}: {after.Output}"
            );
        }
        Program.Check(true, "Metadata and repair leave process privileges unchanged");
    }

    private static void ExpectDenied(Action action)
    {
        try
        {
            action();
            throw new InvalidOperationException("The native fixture must reproduce denied access.");
        }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task<(int Code, string Output)> RunTool(string name, string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, name))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in args)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new IOException("Native fixture tool failed to start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }
}
