using LockscreenGif.Models;
using LockscreenGif.Privileged;
using LockscreenGif.Services.Lockscreen;

sealed class CacheFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "LockscreenGif-apply-tests-" + Guid.NewGuid().ToString("N"));
    public string Folder => Path.Combine(Root, "LockScreen_A");
    public string MainImage => Path.Combine(Folder, "LockScreen.jpg");
    public string Source => Path.Combine(Root, "source.gif");
    public CachePermissions Permissions { get; }
    private readonly LockscreenApplyPipeline _pipeline;

    public CacheFixture(
        bool createFolder = true,
        Func<WindowsImageFeatureState>? readFeature = null,
        ICachePermissionSession? permissionSession = null
    )
    {
        Directory.CreateDirectory(Root);
        if (createFolder)
        {
            Directory.CreateDirectory(Folder);
        }
        // Valid small GIF fixture; no test invokes Windows image APIs or access repair.
        File.WriteAllBytes(Source, Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7"));
        Permissions = new CachePermissions(Root, "S-1-5-21-0-0-0-1000", allowElevation: false, borrowedSession: permissionSession);
        _pipeline = new LockscreenApplyPipeline(
            new CacheLayout(Root, Permissions),
            new VerifiedCacheWriter(Permissions),
            readFeature
                ?? (
                    () =>
                        new WindowsImageFeatureState
                        {
                            QueryStatus = 0,
                            RuntimeState = 1,
                            OverrideExists = false,
                        }
                )
        );
    }

    public Task<LockscreenApplyResult> Apply(Action<LockscreenApplyEvent>? progress = null, CancellationToken token = default) =>
        _pipeline.ApplyAsync(Source, false, new ApplyProgress(progress), token);

    public Task<LockscreenGif.Services.DeleteFilesResult> Remove() =>
        new CacheRemover(new CacheLayout(Root, Permissions), Permissions).RemoveAsync();

    public void Dispose()
    {
        // Only remove the exact uniquely generated test directory inside the temp directory.
        var absolute = Path.GetFullPath(Root);
        var expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (
            Path.GetDirectoryName(absolute) != expectedParent
            || !Path.GetFileName(absolute).StartsWith("LockscreenGif-apply-tests-", StringComparison.Ordinal)
        )
        {
            throw new InvalidOperationException("Unexpected test cleanup path.");
        }

        foreach (var file in Directory.GetFiles(absolute, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(absolute, recursive: true);
    }
}
