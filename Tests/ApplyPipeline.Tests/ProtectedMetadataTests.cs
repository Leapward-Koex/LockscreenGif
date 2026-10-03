using LockscreenGif.Services.Lockscreen;
using LockscreenGif.Tests;

internal static class ProtectedMetadataTests
{
    public static async Task RunAsync()
    {
        using var fixture = new ProtectedCacheFixture();
        ExpectDenied(() => File.GetAttributes(fixture.Parent));
        ExpectDenied(() => File.WriteAllText(fixture.Image, "must not write"));
        Check(Directory.GetDirectories(fixture.Root).Length == 1, "Known cache children remain readable under protected parents.");

        var helper = new FixtureSession(fixture);
        var created = 0;
        await using var permissions = new CachePermissions(
            fixture.Root,
            "S-1-5-21-0-0-0-1000",
            repairSessionFactory: () =>
            {
                created++;
                return helper;
            }
        );
        var layout = new CacheLayout(fixture.Root, permissions);
        await layout.FindDestinationsAsync(new(null), default);
        Check(
            created == 1 && helper.Requests.SequenceEqual(new[] { (fixture.Root, false) }),
            "Protected parent metadata must reach the permission helper during discovery."
        );
        var pipeline = new LockscreenApplyPipeline(
            layout,
            new VerifiedCacheWriter(permissions),
            () =>
                new()
                {
                    QueryStatus = 0,
                    RuntimeState = 1,
                    OverrideExists = false,
                }
        );
        var result = await pipeline.ApplyAsync(fixture.Source, false, new(null), default);
        Check(
            result.Success && result.Files.All(file => file.Copied && file.Verified),
            "A real denied write reaches repair and verified copies beneath protected ancestors."
        );
        Check(
            created == 1 && helper.Requests.Any(request => request == (fixture.Folder, true)),
            "Actual denied staging requests one helper and scoped folder Modify access."
        );
        Check(
            helper.Requests.All(request =>
                request.Item1 == fixture.Root
                || request.Item1.StartsWith(fixture.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ),
            "Client repair requests remain inside the cache; the helper owns metadata scope."
        );
        Check(
            (File.GetAttributes(fixture.Parent) & FileAttributes.Directory) != 0,
            "Post-repair validation uses metadata access without listing the protected parent."
        );
        ExpectDenied(() => Directory.GetDirectories(fixture.Parent));
        await using var linked = new CachePermissions(fixture.Link, "S-1-5-21-0-0-0-1000", allowElevation: false);
        try
        {
            linked.ValidatePath(fixture.Link);
            throw new Exception("Protected reparse point was accepted.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException) { }
    }

    private static void ExpectDenied(Action action)
    {
        try
        {
            action();
            throw new Exception("The protected fixture did not reproduce access denied.");
        }
        catch (UnauthorizedAccessException) { }
    }

    private static void Check(bool value, string message) => PermissionSessionTests.Check(value, message);

    private sealed class FixtureSession(ProtectedCacheFixture fixture) : ICachePermissionSession
    {
        public List<(string, bool)> Requests { get; } = [];

        public Task<int> GrantAsync(string path, bool write, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add((path, write));
            fixture.GrantParentMetadata();
            fixture.Grant(path, write);
            return Task.FromResult(0);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
