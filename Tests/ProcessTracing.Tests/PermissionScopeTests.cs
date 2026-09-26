using LockscreenGif.Privileged.Helper;

namespace ProcessTracing.Tests;

internal static class PermissionScopeTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "LockscreenGif-repair-tests-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "LockScreen_A");
        Directory.CreateDirectory(folder);
        var image = Path.Combine(folder, "LockScreen.jpg");
        await File.WriteAllTextAsync(image, "unchanged");
        var calls = new List<(string Tool, string[] Args)>();
        var codes = new Queue<int>();
        const string sid = "S-1-5-21-0-0-0-1000";
        var repair = new CacheAccessRepair(
            sid,
            root,
            (tool, args) =>
            {
                calls.Add((tool, args));
                return Task.FromResult(codes.Dequeue());
            }
        );
        try
        {
            codes.Enqueue(0);
            Program.Check(await repair.GrantAsync(image, true) == 0 && calls.Count == 1, "Compiled worker repairs only a failed path");
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
            var count = calls.Count;
            foreach (
                var (path, write) in new[]
                {
                    (root, true),
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
        }
    }
}
