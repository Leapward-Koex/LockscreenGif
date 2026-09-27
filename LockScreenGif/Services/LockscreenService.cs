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
    private readonly LockscreenPreferences _preferences;
    private int _applying;
    private SelectedImage? _selectedImage;

    public LockscreenService(AnalyticsService analytics, LockscreenPreferences preferences)
    {
        _analytics = analytics;
        _preferences = preferences;
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

    public void SetCurrentImage(StorageFile file, LockscreenSourceKind sourceKind) =>
        _selectedImage = new(file, new LockscreenSource(file.Path, sourceKind));

    public StorageFile? CurrentImage => _selectedImage?.File;
    public LockscreenSource? CurrentSource => _selectedImage?.Source;
    public BitmapImage? CurrentImageBitmap => CurrentImage is null ? null : new BitmapImage { UriSource = new Uri(CurrentImage.Path) };
    public bool IsApplying => Volatile.Read(ref _applying) != 0;
    public string CacheDirectory { get; }

    public async Task<LockscreenApplyResult> ApplyGifAsLockscreenAsync()
    {
        var source = CurrentSource;
        return source is null
            ? new LockscreenApplyResult { Error = "Choose a GIF before applying." }
            : await ApplyTrackedAsync(
                source.Path,
                _preferences.UseWindowsApi,
                null,
                default,
                null,
                AnalyticsWorkflow.Lockscreen,
                source.Kind
            );
    }

    /// <summary>Detailed apply entry point for Diagnostics; normal user applies use ApplyGifAsLockscreenAsync.</summary>
    public Task<LockscreenApplyResult> ApplyAsync(
        string sourcePath,
        bool useWindowsApi,
        Action<LockscreenApplyEvent>? progress = null,
        CancellationToken cancellationToken = default,
        ICachePermissionSession? permissionSession = null,
        LockscreenSourceKind sourceKind = LockscreenSourceKind.Unknown
    ) =>
        ApplyTrackedAsync(
            sourcePath,
            useWindowsApi,
            progress,
            cancellationToken,
            permissionSession,
            AnalyticsWorkflow.Diagnostics,
            sourceKind
        );

    private Task<LockscreenApplyResult> ApplyTrackedAsync(
        string sourcePath,
        bool useWindowsApi,
        Action<LockscreenApplyEvent>? progress,
        CancellationToken cancellationToken,
        ICachePermissionSession? permissionSession,
        AnalyticsWorkflow workflow,
        LockscreenSourceKind sourceKind
    ) =>
        LockscreenApplyAnalytics.RunAsync(
            _analytics,
            workflow,
            sourceKind,
            useWindowsApi,
            () => ApplyCoreAsync(sourcePath, useWindowsApi, progress, cancellationToken, permissionSession)
        );

    private sealed record SelectedImage(StorageFile File, LockscreenSource Source);

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
            var completed = new AnalyticsProperties
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
            };
            _analytics.Track(AnalyticsEvent.LockscreenRemovalCompleted, completed);
            if (result.FailedDeletions > 0 && result.FailureException is { } failure)
            {
                _analytics.CaptureException(failure, AnalyticsErrorContext.LockscreenRemoval, completed);
            }
            return result;
        }
        catch (Exception ex)
        {
            Logger.Error($"Removing lock-screen variants failed: {ApplyProgress.Describe(ex)}", ex);
            _analytics.TrackFailure(
                AnalyticsEvent.LockscreenRemovalCompleted,
                ex,
                new AnalyticsProperties
                {
                    DurationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
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
