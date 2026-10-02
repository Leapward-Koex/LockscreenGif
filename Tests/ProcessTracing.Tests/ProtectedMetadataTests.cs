using LockscreenGif.Privileged.Helper;
using LockscreenGif.Tests;

namespace ProcessTracing.Tests;

internal static class ProtectedMetadataTests
{
    public static async Task RunAsync()
    {
        using var fixture = new ProtectedCacheFixture();
        var metadataReads = new List<string>();
        var calls = new List<(string, string[])>();
        var repair = new CacheAccessRepair(
            "S-1-5-21-0-0-0-1000",
            fixture.Root,
            (tool, args) =>
            {
                calls.Add((tool, args));
                return Task.FromResult(0);
            },
            readAttributes: path =>
            {
                metadataReads.Add(path);
                // Model the privileged metadata read without enabling privileges
                // in isolated tests. The ordinary client is covered by real ACLs.
                return path == fixture.Parent ? FileAttributes.Directory : File.GetAttributes(path);
            },
            grantParentMetadata: fixture.GrantParentMetadata
        );
        await repair.GrantAsync(fixture.Image, true);
        Program.Check(
            metadataReads.Contains(fixture.Parent) && calls.Count == 1,
            "Protected parent metadata is validated before scoped tool calls"
        );
        calls.Clear();
        var revealedLink = new CacheAccessRepair(
            "S-1-5-21-0-0-0-1000",
            fixture.Root,
            (tool, args) =>
            {
                calls.Add((tool, args));
                return Task.FromResult(0);
            },
            readAttributes: path =>
                path == fixture.Parent ? FileAttributes.Directory | FileAttributes.ReparsePoint : File.GetAttributes(path)
        );
        try
        {
            await revealedLink.GrantAsync(fixture.Image, true);
            throw new Exception("Protected ancestor link was accepted.");
        }
        catch (UnauthorizedAccessException) { }
        Program.Check(calls.Count == 0, "A link revealed by protected metadata blocks ownership and grants");

        var denied = new CacheAccessRepair(
            "S-1-5-21-0-0-0-1000",
            fixture.Root,
            (tool, args) =>
            {
                calls.Add((tool, args));
                return Task.FromResult(0);
            },
            readAttributes: _ => throw new UnauthorizedAccessException("Synthetic unavailable metadata privilege.")
        );
        try
        {
            await denied.GrantAsync(fixture.Image, true);
            throw new Exception("Unavailable protected metadata was accepted.");
        }
        catch (UnauthorizedAccessException) { }
        Program.Check(calls.Count == 0, "Unavailable metadata privilege never falls through to ownership changes");
    }
}
