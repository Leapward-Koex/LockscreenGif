using LockscreenGif.Privileged.Helper;

namespace ProcessTracing.Tests;

internal static class PermissionScopeTests
{
    public static async Task RunAsync()
    {
        var parent = Path.Combine(Path.GetTempPath(), "LockscreenGif-repair-tests-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "ReadOnly");
        var folder = Path.Combine(root, "LockScreen_A");
        Directory.CreateDirectory(folder);
        var image = Path.Combine(folder, "LockScreen.jpg");
        await File.WriteAllTextAsync(image, "unchanged");
        var calls = new List<(string Tool, string[] Args)>();
        var codes = new Queue<int>();
        var metadataAttempts = 0;
        var denyMetadata = false;
        const string sid = "S-1-5-21-0-0-0-1000";
        var repair = new CacheAccessRepair(
            sid,
            root,
            (tool, args) =>
            {
                calls.Add((tool, args));
                return Task.FromResult(codes.Dequeue());
            },
            grantParentMetadata: () =>
            {
                metadataAttempts++;
                if (denyMetadata)
                {
                    denyMetadata = false;
                    throw new UnauthorizedAccessException("Synthetic protected parent DACL.");
                }
            }
        );
        try
        {
            codes.Enqueue(0);
            Program.Check(
                await repair.GrantAsync(image, true) == 0 && calls.Count == 1 && metadataAttempts == 1,
                "Worker repairs SID metadata and the requested cache path"
            );
            Program.Check(
                calls[0].Args.SequenceEqual(new[] { image, "/grant", "*" + sid + ":M" }),
                "Only the requesting SID receives scoped Modify access"
            );
            calls.Clear();
            foreach (var code in new[] { 1, 0, 0 })
            {
                codes.Enqueue(code);
            }

            await repair.GrantAsync(folder, false);
            Program.Check(
                calls.Count == 3 && calls[1].Args.SequenceEqual(new[] { "/f", folder, "/a" }) && calls[2].Args.Last() == "*" + sid + ":RX",
                "Ownership fallback is nonrecursive and preserves read rights"
            );
            calls.Clear();
            denyMetadata = true;
            var previousAttempts = metadataAttempts;
            foreach (var code in new[] { 0, 0 })
            {
                codes.Enqueue(code);
            }
            await repair.GrantAsync(root, false);
            Program.Check(
                calls.Count == 2
                    && calls[0].Args.SequenceEqual(new[] { "/f", parent, "/a" })
                    && metadataAttempts == previousAttempts + 2
                    && calls[1].Args.SequenceEqual(new[] { root, "/grant", "*" + sid + ":RX" }),
                "Protected SID ownership fallback grants only non-inherited ReadAttributes before repairing the cache"
            );
            calls.Clear();
            denyMetadata = true;
            codes.Enqueue(5);
            Program.Check(
                await repair.GrantAsync(root, false) == 5 && calls.Count == 1,
                "Failed metadata repair stops before cache grants"
            );
            var count = calls.Count;
            foreach (
                var (path, write) in new[]
                {
                    (root, true),
                    (parent, false),
                    (Path.Combine(root, "..", "outside"), false),
                    (Path.Combine(folder, "private.txt"), true),
                    (Path.Combine(folder, "nested", "LockScreen.jpg"), true),
                }
            )
            {
                try
                {
                    await repair.GrantAsync(path, write);
                    throw new Exception("Invalid scope accepted.");
                }
                catch (UnauthorizedAccessException) { }
            }
            Program.Check(
                calls.Count == count && await File.ReadAllTextAsync(image) == "unchanged",
                "Invalid scopes never execute a native tool or change bytes"
            );
            var link = Path.Combine(root, "LockScreen_Link");
            Directory.CreateSymbolicLink(link, folder);
            try
            {
                try
                {
                    repair.Validate(link, false);
                    throw new Exception("Reparse point accepted.");
                }
                catch (UnauthorizedAccessException)
                {
                    Program.Check(true, "Reparse points are rejected before access repair");
                }
            }
            finally
            {
                Directory.Delete(link);
            }
        }
        finally
        {
            File.Delete(image);
            Directory.Delete(folder);
            Directory.Delete(root);
            Directory.Delete(parent);
        }
    }
}
