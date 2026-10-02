using LockscreenGif.Models;
using LockscreenGif.Services.Analytics;

internal static class ApplyFailureTests
{
    private const string PrivateDetail = @"synthetic-private C:\Users\private-person\private-animation.gif";

    private static readonly (LockscreenApplyFailureReason Reason, string WireValue)[] Reasons =
    [
        (LockscreenApplyFailureReason.SourceReadFailed, "source_read_failed"),
        (LockscreenApplyFailureReason.InvalidSource, "invalid_source"),
        (LockscreenApplyFailureReason.WindowsApiFailed, "windows_api_failed"),
        (LockscreenApplyFailureReason.CacheInaccessible, "cache_inaccessible"),
        (LockscreenApplyFailureReason.CacheMissing, "cache_missing"),
        (LockscreenApplyFailureReason.NoDestinations, "no_destinations"),
        (LockscreenApplyFailureReason.CacheDiscoveryFailed, "cache_discovery_failed"),
        (LockscreenApplyFailureReason.CopyFailed, "copy_failed"),
        (LockscreenApplyFailureReason.VerificationFailed, "verification_failed"),
        (LockscreenApplyFailureReason.Unknown, "unknown"),
    ];

    public static async Task RunAsync()
    {
        await ZeroTargetFailuresAsync();
        await OutcomeReasonsAsync();
        await FailureReasonAllowlistAsync();
        await DeliveryAndConsentAsync();
        Console.WriteLine("PASS apply failure categories distinguish empty, absent and inaccessible caches without leaking private data");
    }

    private static async Task ZeroTargetFailuresAsync()
    {
        foreach (var workflow in Enum.GetValues<AnalyticsWorkflow>())
        {
            using var context = new TestContext(blockFirst: true);
            using var analytics = context.Create();
            Check(analytics.SetEnabled(true), "Analytics enabled for the fake transport.");
            analytics.Track(AnalyticsEvent.AppOpened);
            await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var cases = new (LockscreenApplyFailureReason Reason, string WireValue, Exception? Error)[]
            {
                (LockscreenApplyFailureReason.CacheInaccessible, "cache_inaccessible", new UnauthorizedAccessException(PrivateDetail)),
                (LockscreenApplyFailureReason.CacheMissing, "cache_missing", new DirectoryNotFoundException(PrivateDetail)),
                (LockscreenApplyFailureReason.NoDestinations, "no_destinations", null),
            };
            foreach (var item in cases)
            {
                var result = new LockscreenApplyResult
                {
                    FailureReason = item.Reason,
                    FailureException = item.Error,
                    Error = PrivateDetail,
                    ApiRequested = true,
                    ApiCompleted = true,
                };
                var actual = await LockscreenApplyAnalytics.RunAsync(
                    analytics,
                    workflow,
                    LockscreenSourceKind.UserGif,
                    true,
                    () => Task.FromResult(result)
                );
                Check(ReferenceEquals(actual, result), "Tracking returns the original failed result.");
            }

            context.Handler.Release.TrySetResult();
            await analytics.ShutdownAsync();
            var requests = context.Handler.Requests.Skip(1).ToArray();
            var starts = requests.Where(request => EventName(request) == "lockscreen_apply_started").ToArray();
            var completions = requests.Where(request => EventName(request) == "lockscreen_apply_completed").ToArray();
            var exceptions = requests.Where(request => EventName(request) == "$exception").ToArray();
            Check(starts.Length == cases.Length && completions.Length == cases.Length, "Each attempt emits one start and completion.");
            Check(exceptions.Length == 2, "Only results with actual exceptions emit companion exception events.");
            for (var index = 0; index < cases.Length; index++)
            {
                var completed = completions[index];
                var operationId = starts[index].Properties.GetProperty("operation_id").GetGuid();
                Check(completed.Properties.GetProperty("operation_id").GetGuid() == operationId, "Completion retains the attempt ID.");
                Check(
                    completed.Properties.GetProperty("outcome").GetString() == "failed",
                    "Zero discovered targets still produce a failed outcome."
                );
                Check(completed.Properties.GetProperty("api_completed").GetBoolean(), "API completion does not override cache failure.");
                foreach (var name in new[] { "target_count", "copied_count", "verified_count", "failed_count" })
                {
                    Check(
                        completed.Properties.GetProperty(name).GetInt32() == 0,
                        "Failure before target discovery preserves zero file counts."
                    );
                }
                Check(
                    completed.Properties.GetProperty("apply_failure_reason").GetString() == cases[index].WireValue,
                    "Cache failure categories remain distinct."
                );
                var companion = exceptions.SingleOrDefault(request =>
                    request.Properties.GetProperty("operation_id").GetGuid() == operationId
                );
                if (companion is not null)
                {
                    Check(
                        companion.Properties.GetProperty("apply_failure_reason").GetString() == cases[index].WireValue,
                        "The companion exception retains the same reason."
                    );
                    Check(
                        companion.Properties.GetProperty("outcome").GetString() == "failed",
                        "The companion exception retains the failed outcome."
                    );
                }
            }
            Check(
                starts.All(request => !request.Properties.TryGetProperty("apply_failure_reason", out _)),
                "Start events do not predict failure."
            );
            Check(
                requests.All(request =>
                    request.Properties.GetProperty("workflow").GetString()
                    == (workflow == AnalyticsWorkflow.Diagnostics ? "diagnostics" : "lockscreen")
                ),
                "Diagnostic and normal apply workflows remain separate."
            );
            Check(
                requests.All(request => !request.Raw.Contains("private", StringComparison.OrdinalIgnoreCase)),
                "Paths and exception/result text never enter telemetry."
            );
        }
    }

    private static async Task OutcomeReasonsAsync()
    {
        using var context = new TestContext(blockFirst: true);
        using var analytics = context.Create();
        Check(analytics.SetEnabled(true), "Analytics enabled.");
        analytics.Track(AnalyticsEvent.AppOpened);
        await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var cases = new (LockscreenApplyResult Result, string Outcome, string? Reason)[]
        {
            (
                new()
                {
                    FailureReason = LockscreenApplyFailureReason.VerificationFailed,
                    FailureException = new IOException(PrivateDetail),
                    Files = [new() { Copied = true, Verified = false }],
                },
                "partial",
                "verification_failed"
            ),
            (new() { Error = PrivateDetail }, "failed", "unknown"),
            (new() { Success = true, FailureReason = LockscreenApplyFailureReason.CopyFailed }, "succeeded", null),
            (new() { Cancelled = true, FailureReason = LockscreenApplyFailureReason.CopyFailed }, "cancelled", null),
        };
        foreach (var item in cases)
        {
            await LockscreenApplyAnalytics.RunAsync(
                analytics,
                AnalyticsWorkflow.Lockscreen,
                LockscreenSourceKind.Video,
                false,
                () => Task.FromResult(item.Result)
            );
        }
        foreach (
            var error in new Exception[] { new UnauthorizedAccessException(PrivateDetail), new OperationCanceledException(PrivateDetail) }
        )
        {
            try
            {
                await LockscreenApplyAnalytics.RunAsync(
                    analytics,
                    AnalyticsWorkflow.Lockscreen,
                    LockscreenSourceKind.Video,
                    false,
                    () => Task.FromException<LockscreenApplyResult>(error)
                );
                throw new InvalidOperationException("The original apply exception was swallowed.");
            }
            catch (Exception actual) when (ReferenceEquals(actual, error)) { }
        }

        context.Handler.Release.TrySetResult();
        await analytics.ShutdownAsync();
        var requests = context.Handler.Requests.Skip(1).ToArray();
        var completions = requests.Where(request => EventName(request) == "lockscreen_apply_completed").ToArray();
        Check(completions.Length == cases.Length + 2, "Returned results and thrown errors each produce one terminal event.");
        for (var index = 0; index < cases.Length; index++)
        {
            Check(
                completions[index].Properties.GetProperty("outcome").GetString() == cases[index].Outcome,
                "Reason reporting preserves the terminal outcome."
            );
            AssertReason(completions[index], cases[index].Reason);
        }
        AssertReason(completions[^2], "unknown");
        AssertReason(completions[^1], null);
        Check(completions[^1].Properties.GetProperty("outcome").GetString() == "cancelled", "Thrown cancellation remains cancellation.");
        var exceptions = requests.Where(request => EventName(request) == "$exception").ToArray();
        Check(exceptions.Length == 2, "Partial failure and thrown error each report one exception; cancellation reports none.");
        AssertReason(exceptions[0], "verification_failed");
        AssertReason(exceptions[1], "unknown");
        Check(
            exceptions[0].Properties.GetProperty("outcome").GetString() == "partial",
            "Error reporting must preserve partial completion."
        );
    }

    private static async Task FailureReasonAllowlistAsync()
    {
        using var context = new TestContext(blockFirst: true);
        using var analytics = context.Create();
        Check(analytics.SetEnabled(true), "Analytics enabled.");
        analytics.Track(AnalyticsEvent.AppOpened);
        await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(Reasons.Length == Enum.GetValues<LockscreenApplyFailureReason>().Length, "Every model reason has a deliberate wire mapping.");
        foreach (var item in Reasons)
        {
            analytics.Track(AnalyticsEvent.LockscreenApplyCompleted, new AnalyticsProperties { ApplyFailureReason = item.Reason });
        }
        analytics.Track(
            AnalyticsEvent.LockscreenApplyCompleted,
            new AnalyticsProperties { ApplyFailureReason = (LockscreenApplyFailureReason)999 }
        );
        analytics.Track(AnalyticsEvent.LockscreenApplyCompleted, new AnalyticsProperties());
        context.Handler.Release.TrySetResult();
        await analytics.ShutdownAsync();
        var requests = context.Handler.Requests.Skip(1).ToArray();
        Check(requests.Length == Reasons.Length + 2, "Every allowlist test reached the fake transport.");
        for (var index = 0; index < Reasons.Length; index++)
        {
            AssertReason(requests[index], Reasons[index].WireValue);
        }
        AssertReason(requests[^2], null);
        AssertReason(requests[^1], null);
    }

    private static async Task DeliveryAndConsentAsync()
    {
        foreach (var failFirst in new[] { false, true })
        {
            using var context = new TestContext(blockFirst: true, failFirst: failFirst);
            using var analytics = context.Create();
            Check(analytics.SetEnabled(true), "Analytics enabled.");
            var result = new LockscreenApplyResult
            {
                FailureReason = LockscreenApplyFailureReason.CacheInaccessible,
                FailureException = new UnauthorizedAccessException(PrivateDetail),
            };
            try
            {
                var actual = await LockscreenApplyAnalytics
                    .RunAsync(
                        analytics,
                        AnalyticsWorkflow.Lockscreen,
                        LockscreenSourceKind.UserGif,
                        false,
                        async () =>
                        {
                            await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                            return result;
                        }
                    )
                    .WaitAsync(TimeSpan.FromSeconds(2));
                Check(ReferenceEquals(actual, result), "Blocked/failing telemetry does not change or delay the apply result.");
                Check(!context.Handler.Release.Task.IsCompleted, "The apply completes while telemetry is still blocked.");
            }
            finally
            {
                context.Handler.Release.TrySetResult();
            }
            await analytics.ShutdownAsync();
            var completed = context.Handler.Requests.Single(request => EventName(request) == "lockscreen_apply_completed");
            AssertReason(completed, "cache_inaccessible");
        }

        using var optedOut = new TestContext();
        using var disabledAnalytics = optedOut.Create();
        Check(disabledAnalytics.SetEnabled(false), "Opt-out saved.");
        await LockscreenApplyAnalytics.RunAsync(
            disabledAnalytics,
            AnalyticsWorkflow.Lockscreen,
            LockscreenSourceKind.UserGif,
            false,
            () =>
                Task.FromResult(
                    new LockscreenApplyResult
                    {
                        FailureReason = LockscreenApplyFailureReason.CacheInaccessible,
                        FailureException = new UnauthorizedAccessException(PrivateDetail),
                    }
                )
        );
        await disabledAnalytics.ShutdownAsync();
        Check(optedOut.Handler.Requests.IsEmpty, "Failure reasons and companion exceptions respect opt-out.");
    }

    private static string? EventName(CapturedRequest request) => request.Root.GetProperty("event").GetString();

    private static void AssertReason(CapturedRequest request, string? expected)
    {
        var present = request.Properties.TryGetProperty("apply_failure_reason", out var actual);
        Check(expected is null ? !present : present && actual.GetString() == expected, "Only the expected bounded failure reason is sent.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
