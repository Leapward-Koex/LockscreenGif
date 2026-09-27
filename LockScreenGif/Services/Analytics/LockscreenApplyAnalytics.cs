using System.Diagnostics;
using LockscreenGif.Models;

namespace LockscreenGif.Services.Analytics;

internal static class LockscreenApplyAnalytics
{
    public static async Task<LockscreenApplyResult> RunAsync(
        AnalyticsService analytics,
        AnalyticsWorkflow workflow,
        LockscreenSourceKind sourceKind,
        bool useWindowsApi,
        Func<Task<LockscreenApplyResult>> apply
    )
    {
        var started = Stopwatch.GetTimestamp();
        // Reuse this immutable attempt snapshot on every terminal path, including queued/cancelled applies.
        var operation = new AnalyticsProperties
        {
            OperationId = Guid.NewGuid(),
            Workflow = workflow,
            LockscreenSource = sourceKind,
            ApiRequested = useWindowsApi,
        };
        analytics.Track(AnalyticsEvent.LockscreenApplyStarted, operation);
        try
        {
            var result = await apply();
            var completed = operation with
            {
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
            };
            analytics.Track(AnalyticsEvent.LockscreenApplyCompleted, completed);
            if (!result.Cancelled && !result.Success && result.FailureException is { } failure)
            {
                analytics.CaptureException(failure, AnalyticsErrorContext.LockscreenApply, completed);
            }
            return result;
        }
        catch (Exception ex)
        {
            analytics.TrackFailure(
                AnalyticsEvent.LockscreenApplyCompleted,
                ex,
                operation with
                {
                    DurationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                }
            );
            throw;
        }
    }
}
