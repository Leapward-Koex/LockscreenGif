using System.Security.Principal;
using LockscreenGif.Contracts.Services;
using LockscreenGif.Models;
using LockscreenGif.Services.Lockscreen;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;

namespace LockscreenGif.Services;

/// <summary>Serializes changes to the current user's lock-screen cache.</summary>
public sealed class LockscreenService : ILockscreenService
{
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly string _userSid;
    private int _applying;

    public LockscreenService()
    {
        using var identity = WindowsIdentity.GetCurrent();
        _userSid = identity.User?.Value ?? throw new InvalidOperationException("Unable to obtain the current user's SID.");
        CacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft",
            "Windows",
            "SystemData",
            _userSid,
            "ReadOnly"
        );
    }

    public StorageFile? CurrentImage { get; set; }
    public BitmapImage? CurrentImageBitmap => CurrentImage is null ? null : new BitmapImage { UriSource = new Uri(CurrentImage.Path) };
    public bool IsApplying => Volatile.Read(ref _applying) != 0;
    public string CacheDirectory { get; }

    public async Task<bool> ApplyGifAsLockscreenAsync()
    {
        var source = CurrentImage;
        return source is not null && (await ApplyAsync(source.Path, false)).Success;
    }

    public async Task<LockscreenApplyResult> ApplyAsync(
        string sourcePath,
        bool useWindowsApi,
        Action<LockscreenApplyEvent>? progress = null,
        CancellationToken cancellationToken = default,
        ICachePermissionSession? permissionSession = null
    )
    {
        var reporter = new ApplyProgress(progress);
        try
        {
            await _operationLock.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException ex)
        {
            reporter.Report("Cancelled", ex.Message, severity: "Warning");
            return new LockscreenApplyResult
            {
                Cancelled = true,
                ApiRequested = useWindowsApi,
                Error = ex.Message,
            };
        }
        try
        {
            Volatile.Write(ref _applying, 1);
            await using var permissions = new CachePermissions(CacheDirectory, _userSid, borrowedSession: permissionSession);
            var pipeline = new LockscreenApplyPipeline(new CacheLayout(CacheDirectory, permissions), new VerifiedCacheWriter(permissions));
            return await pipeline.ApplyAsync(sourcePath, useWindowsApi, reporter, cancellationToken);
        }
        finally
        {
            Volatile.Write(ref _applying, 0);
            _operationLock.Release();
        }
    }

    public async Task WaitForIdleAsync()
    {
        await _operationLock.WaitAsync();
        _operationLock.Release();
    }

    public async Task<DeleteFilesResult?> RemoveAppliedGif()
    {
        await _operationLock.WaitAsync();
        try
        {
            Volatile.Write(ref _applying, 1);
            await using var permissions = new CachePermissions(CacheDirectory, _userSid);
            var remover = new CacheRemover(new CacheLayout(CacheDirectory, permissions), permissions);
            var result = await remover.RemoveAsync();
            Logger.Info($"Deleted lock-screen variants: {result.SuccessfulDeletions} succeeded, {result.FailedDeletions} failed.");
            return result;
        }
        catch (Exception ex)
        {
            Logger.Error($"Removing lock-screen variants failed: {ApplyProgress.Describe(ex)}", ex);
            return null;
        }
        finally
        {
            Volatile.Write(ref _applying, 0);
            _operationLock.Release();
        }
    }

    public enum LockScreenMode
    {
        PictureOrOther,
        Slideshow,
        Spotlight,
        Unknown,
    }

    /// <summary>Returns a registry heuristic, not a confirmed Windows lock-screen mode.</summary>
    public static LockScreenMode TryGetLockScreenMode() => LockscreenSettings.InferMode();
}
