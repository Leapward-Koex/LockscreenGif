using System.Diagnostics;
using System.Security.Principal;
using LockscreenGif.Contracts.Services;
using LockscreenGif.Models;
using LockscreenGif.Services.Analytics;
using LockscreenGif.Services.Lockscreen;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;

namespace LockscreenGif.Services;

/// <summary>Serializes changes to the current user's lock-screen cache.</summary>
public sealed class LockscreenService : ILockscreenService
{
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly string _userSid;
    private readonly AnalyticsService _analytics;
    private int _applying;

    public LockscreenService(AnalyticsService analytics)
    {
        _analytics = analytics;
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

    public async Task<LockscreenApplyResult> ApplyGifAsLockscreenAsync()
    {
        var source = CurrentImage;
        return source is null
            ? new LockscreenApplyResult { Error = "Choose a GIF before applying." }
            : await ApplyTrackedAsync(source.Path, false, null, default, null, AnalyticsWorkflow.Lockscreen);
    }

    /// <summary>Detailed apply entry point for Diagnostics; normal user applies use ApplyGifAsLockscreenAsync.</summary>
    public Task<LockscreenApplyResult> ApplyAsync(
        string sourcePath,
        bool useWindowsApi,
        Action<LockscreenApplyEvent>? progress = null,
        CancellationToken cancellationToken = default,
        ICachePermissionSession? permissionSession = null
    ) => ApplyTrackedAsync(sourcePath, useWindowsApi, progress, cancellationToken, permissionSession, AnalyticsWorkflow.Diagnostics);

    private async Task<LockscreenApplyResult> ApplyTrackedAsync(
        string sourcePath,
        bool useWindowsApi,
        Action<LockscreenApplyEvent>? progress,
        CancellationToken cancellationToken,
        ICachePermissionSession? permissionSession,
        AnalyticsWorkflow workflow
    )
    {
        var started = Stopwatch.GetTimestamp();
        var operationId = Guid.NewGuid();
        _analytics.Track(
            AnalyticsEvent.LockscreenApplyStarted,
            new()
            {
                OperationId = operationId,
                Workflow = workflow,
                ApiRequested = useWindowsApi,
            }
        );
        try
        {
            var result = await ApplyCoreAsync(sourcePath, useWindowsApi, progress, cancellationToken, permissionSession);
            _analytics.Track(
                AnalyticsEvent.LockscreenApplyCompleted,
                new()
                {
                    OperationId = operationId,
                    Workflow = workflow,
                    Outcome =
                        result.Cancelled ? AnalyticsOutcome.Cancelled
                        : result.Success ? AnalyticsOutcome.Succeeded
                        : result.Files.Any(file => file.Copied) ? AnalyticsOutcome.Partial
                        : AnalyticsOutcome.Failed,
                    DurationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    TargetCount = result.Files.Count,
                    CopiedCount = result.Files.Count(file => file.Copied),
                    VerifiedCount = result.Files.Count(file => file.Verified),
                    FailedCount = result.Cancelled ? null : result.Files.Count(file => !file.Copied || !file.Verified),
                    ApiRequested = result.ApiRequested,
                    ApiCompleted = result.ApiCompleted,
                    GifSizeBytes = result.SourceSizeBytes,
                    GifWidth = result.SourceWidth,
                    GifHeight = result.SourceHeight,
                }
            );
            return result;
        }
        catch (Exception ex)
        {
            _analytics.Track(
                AnalyticsEvent.LockscreenApplyCompleted,
                new()
                {
                    OperationId = operationId,
                    Workflow = workflow,
                    Outcome = ex is OperationCanceledException ? AnalyticsOutcome.Cancelled : AnalyticsOutcome.Failed,
                    DurationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    ApiRequested = useWindowsApi,
                    ErrorKind = AnalyticsProperties.ClassifyError(ex),
                }
            );
            throw;
        }
    }

    private async Task<LockscreenApplyResult> ApplyCoreAsync(
        string sourcePath,
        bool useWindowsApi,
        Action<LockscreenApplyEvent>? progress,
        CancellationToken cancellationToken,
        ICachePermissionSession? permissionSession
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
        var started = Stopwatch.GetTimestamp();
        await _operationLock.WaitAsync();
        try
        {
            Volatile.Write(ref _applying, 1);
            DeleteFilesResult result;
            await using (var permissions = new CachePermissions(CacheDirectory, _userSid))
            {
                var remover = new CacheRemover(new CacheLayout(CacheDirectory, permissions), permissions);
                result = await remover.RemoveAsync();
            }
            Logger.Info($"Deleted lock-screen variants: {result.SuccessfulDeletions} succeeded, {result.FailedDeletions} failed.");
            _analytics.Track(
                AnalyticsEvent.LockscreenRemovalCompleted,
                new()
                {
                    Outcome =
                        result.FailedDeletions > 0
                            ? result.SuccessfulDeletions > 0
                                ? AnalyticsOutcome.Partial
                                : AnalyticsOutcome.Failed
                            : result.SuccessfulDeletions > 0
                                ? AnalyticsOutcome.Succeeded
                                : AnalyticsOutcome.NoChange,
                    DurationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    TargetCount = result.SuccessfulDeletions + result.FailedDeletions,
                    FailedCount = result.FailedDeletions,
                    Workflow = AnalyticsWorkflow.Lockscreen,
                }
            );
            return result;
        }
        catch (Exception ex)
        {
            Logger.Error($"Removing lock-screen variants failed: {ApplyProgress.Describe(ex)}", ex);
            _analytics.Track(
                AnalyticsEvent.LockscreenRemovalCompleted,
                new()
                {
                    Outcome = AnalyticsOutcome.Failed,
                    DurationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    ErrorKind = AnalyticsProperties.ClassifyError(ex),
                    Workflow = AnalyticsWorkflow.Lockscreen,
                }
            );
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
