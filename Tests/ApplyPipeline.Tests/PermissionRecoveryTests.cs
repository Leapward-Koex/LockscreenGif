using LockscreenGif.Models;
using LockscreenGif.Services.Lockscreen;

internal static class PermissionRecoveryTests
{
    public static Task DeniedRootAttributesRecover() => DeniedAttributesRecover(fixture => fixture.Root, write: false);

    public static Task DeniedFolderAttributesRecover() => DeniedAttributesRecover(fixture => fixture.Folder, write: false);

    public static Task DeniedFileAttributesRecover() => DeniedAttributesRecover(fixture => fixture.MainImage, write: true);

    public static Task DeniedCommitAttributesRecover() =>
        DeniedAttributesRecover(fixture => fixture.MainImage, write: true, denyAtCommit: true);

    private static async Task DeniedAttributesRecover(Func<CacheFixture, string> deniedPath, bool write, bool denyAtCommit = false)
    {
        using var fixture = new CacheFixture();
        await File.WriteAllTextAsync(fixture.MainImage, "previous image");
        var path = deniedPath(fixture);
        var repaired = false;
        var deniedReads = 0;
        var revalidated = false;
        var denialEnabled = !denyAtCommit;
        var helper = new FakeSession(() =>
        {
            repaired = true;
            return 0;
        });
        await using var permissions = new CachePermissions(
            fixture.Root,
            FixtureSid,
            repairSessionFactory: () => helper,
            readAttributes: candidate =>
            {
                if (SamePath(candidate, path))
                {
                    if (denialEnabled && !repaired)
                    {
                        deniedReads++;
                        throw new UnauthorizedAccessException("Synthetic protected attributes.");
                    }
                    revalidated |= repaired;
                }
                return File.GetAttributes(candidate);
            }
        );
        var result = await Apply(
            fixture,
            permissions,
            progress: item =>
            {
                if (item.Stage == "Staged" && item.Path == fixture.MainImage)
                {
                    denialEnabled = true;
                }
            }
        );
        Check(result.Success && result.Files.All(file => file.Verified), "Repair must reach verified copies after strict revalidation.");
        Check(deniedReads > 0 && revalidated, "The test must exercise denied metadata and a strict check after repair.");
        Check(
            helper.Requests.SequenceEqual(new[] { (path, write) }),
            "Only the inaccessible cache path and required access may be repaired."
        );
    }

    public static async Task DeniedStagingAttributesRecover()
    {
        using var fixture = new CacheFixture();
        var repaired = false;
        var helper = new FakeSession(() =>
        {
            repaired = true;
            return 0;
        });
        await using var permissions = new CachePermissions(
            fixture.Root,
            FixtureSid,
            repairSessionFactory: () => helper,
            readAttributes: path =>
                !repaired && path.EndsWith(".lockscreen.tmp", StringComparison.Ordinal)
                    ? throw new UnauthorizedAccessException("Synthetic protected staging attributes.")
                    : File.GetAttributes(path)
        );
        var result = await Apply(fixture, permissions);
        Check(
            result.Success && result.Files.All(file => file.Verified),
            "Denied staging attributes must participate in the staging retry."
        );
        Check(
            helper.Requests.SequenceEqual(new[] { (fixture.Folder, true) }),
            "Staging repair requests only its cache folder, never a temporary name."
        );
    }

    public static async Task CommitRepairRevalidatesStagedPath()
    {
        using var fixture = new CacheFixture();
        await File.WriteAllTextAsync(fixture.MainImage, "previous image");
        var stagedPath = Path.Combine(fixture.Folder, ".synthetic.lockscreen.tmp");
        await File.WriteAllTextAsync(stagedPath, "replacement image");
        var repaired = false;
        var helper = new FakeSession(() =>
        {
            repaired = true;
            return 0;
        });
        await using var permissions = new CachePermissions(
            fixture.Root,
            FixtureSid,
            repairSessionFactory: () => helper,
            readAttributes: path =>
            {
                if (!repaired && SamePath(path, fixture.MainImage))
                {
                    throw new UnauthorizedAccessException("Synthetic protected destination attributes.");
                }
                if (repaired && SamePath(path, stagedPath))
                {
                    return FileAttributes.ReparsePoint;
                }
                return File.GetAttributes(path);
            }
        );
        await ExpectInvalidScope(() => new CacheFileCommitter(permissions).CommitAsync(stagedPath, fixture.MainImage, new(null), default));
        Check(
            helper.Requests.SequenceEqual(new[] { (fixture.MainImage, true) }),
            "Commit repair remains scoped to the inaccessible destination."
        );
        Check(
            await File.ReadAllTextAsync(fixture.MainImage) == "previous image" && File.Exists(stagedPath),
            "A staged link revealed after destination repair must be rejected before replacing the previous image."
        );
    }

    public static async Task RejectedRepairMakesNoWrites()
    {
        using var fixture = new CacheFixture();
        var helper = new FakeSession(() => 5);
        await using var permissions = DeniedPermissions(fixture, fixture.Root, helper);
        var result = await Apply(fixture, permissions);
        Check(!result.Success && result.Files.Count == 0, "A helper rejection must stop discovery before cache writes.");
        Check(
            helper.Requests.SequenceEqual(new[] { (fixture.Root, false) }),
            "A rejected root read repair must not escalate scope or retry."
        );
        Check(Directory.GetFiles(fixture.Folder).Length == 0, "Rejected permission repair cannot write cache files.");
    }

    public static async Task StillDeniedAncestorFailsClosed()
    {
        using var fixture = new CacheFixture();
        var helper = new FakeSession(() => 0);
        await using var permissions = DeniedPermissions(fixture, Path.GetDirectoryName(fixture.Root)!, helper);
        var result = await Apply(fixture, permissions);
        Check(!result.Success && result.Files.Count == 0, "An unreadable ancestor must still fail strict validation after scoped repair.");
        Check(
            helper.Requests.SequenceEqual(new[] { (fixture.Root, false) }),
            "Ancestor denial cannot cause ancestor grants or repeat repairs."
        );
        Check(Directory.GetFiles(fixture.Folder).Length == 0, "No writes are allowed while ancestor link checks remain incomplete.");
    }

    public static async Task UnsafePathsNeverReachHelper()
    {
        using var fixture = new CacheFixture();
        var reads = 0;
        var helper = new FakeSession(() => 0);
        await using var permissions = new CachePermissions(
            fixture.Root,
            FixtureSid,
            repairSessionFactory: () => helper,
            readAttributes: path =>
            {
                reads++;
                if (SamePath(path, fixture.Root))
                {
                    throw new UnauthorizedAccessException("Synthetic protected attributes.");
                }
                if (SamePath(path, Path.GetDirectoryName(fixture.Root)!))
                {
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }
                return File.GetAttributes(path);
            }
        );
        await ExpectInvalidScope(() => permissions.GrantAsync(Path.Combine(fixture.Root, "..", "outside"), false, new(null), default));
        Check(reads == 0, "Lexical containment must be checked before inspecting attributes or starting repair.");
        await ExpectInvalidScope(() => permissions.GrantAsync(fixture.Root, false, new(null), default));
        Check(helper.Requests.Count == 0, "A visible ancestor link must be rejected even when another attribute read was denied.");
        Check(Directory.GetFiles(fixture.Folder).Length == 0, "Unsafe paths cannot produce cache writes.");
    }

    public static async Task RepairSuccessStillRejectsLinks()
    {
        using var fixture = new CacheFixture();
        var repaired = false;
        var helper = new FakeSession(() =>
        {
            repaired = true;
            return 0;
        });
        await using var permissions = new CachePermissions(
            fixture.Root,
            FixtureSid,
            repairSessionFactory: () => helper,
            readAttributes: path =>
            {
                if (SamePath(path, fixture.Root))
                {
                    if (!repaired)
                    {
                        throw new UnauthorizedAccessException("Synthetic protected attributes.");
                    }
                    return FileAttributes.Directory | FileAttributes.ReparsePoint;
                }
                return File.GetAttributes(path);
            }
        );
        var result = await Apply(fixture, permissions);
        Check(!result.Success && result.Files.Count == 0, "Successful helper replies cannot replace strict client link validation.");
        Check(
            helper.Requests.Count == 1 && Directory.GetFiles(fixture.Folder).Length == 0,
            "A newly visible link stops writes after one repair."
        );
    }

    public static async Task CancellationNeverStartsRepair()
    {
        using var fixture = new CacheFixture();
        using var cancelled = new CancellationTokenSource();
        var started = 0;
        await using var permissions = new CachePermissions(
            fixture.Root,
            FixtureSid,
            repairSessionFactory: () =>
            {
                started++;
                return new FakeSession(() => 0);
            },
            readAttributes: path =>
            {
                cancelled.Cancel();
                throw new UnauthorizedAccessException("Synthetic protected attributes.");
            }
        );
        var result = await Apply(fixture, permissions, cancelled.Token);
        Check(result.Cancelled && started == 0, "Cancellation detected at denied attributes must not create a permission helper.");
        Check(Directory.GetFiles(fixture.Folder).Length == 0, "Cancelled recovery cannot write cache files.");
    }

    private const string FixtureSid = "S-1-5-21-0-0-0-1000";

    private static CachePermissions DeniedPermissions(CacheFixture fixture, string deniedPath, FakeSession helper) =>
        new(
            fixture.Root,
            FixtureSid,
            repairSessionFactory: () => helper,
            readAttributes: path =>
                SamePath(path, deniedPath)
                    ? throw new UnauthorizedAccessException("Synthetic protected attributes.")
                    : File.GetAttributes(path)
        );

    private static Task<LockscreenApplyResult> Apply(
        CacheFixture fixture,
        CachePermissions permissions,
        CancellationToken token = default,
        Action<LockscreenApplyEvent>? progress = null
    ) =>
        new LockscreenApplyPipeline(
            new CacheLayout(fixture.Root, permissions),
            new VerifiedCacheWriter(permissions),
            () =>
                new()
                {
                    QueryStatus = 0,
                    RuntimeState = 1,
                    OverrideExists = false,
                }
        ).ApplyAsync(fixture.Source, false, new(progress), token);

    private static bool SamePath(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);

    private static async Task ExpectInvalidScope(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException("An unsafe path was accepted.");
    }

    private static void Check(bool condition, string message) => PermissionSessionTests.Check(condition, message);

    private sealed class FakeSession(Func<int> grant) : ICachePermissionSession
    {
        public List<(string Path, bool Write)> Requests { get; } = [];

        public Task<int> GrantAsync(string path, bool write, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add((path, write));
            return Task.FromResult(grant());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
