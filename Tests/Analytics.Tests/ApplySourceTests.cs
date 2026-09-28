using LockscreenGif.Models;
using LockscreenGif.Services.Analytics;

internal static class ApplySourceTests
{
    private const string PrivateDetail = @"synthetic-private C:\Users\private-person\private-animation.gif";

    private static readonly (LockscreenSourceKind Kind, string WireValue)[] Sources =
    [
        (LockscreenSourceKind.Unknown, "unknown"),
        (LockscreenSourceKind.Video, "video"),
        (LockscreenSourceKind.UserGif, "user_gif"),
        (LockscreenSourceKind.BundledGif, "bundled_gif"),
    ];

    public static async Task RunAsync()
    {
        await Run("apply start and result outcomes preserve source and workflow without private data", ResultOutcomesAsync);
        await Run("apply exceptions and cancellation emit one terminal event with the original source", ExceptionOutcomesAsync);
        await Run("returned apply failures report one private-safe exception with operation provenance", ReturnedFailuresAsync);
        await Run("apply source is snapshotted before asynchronous work changes selection", SourceSnapshotAsync);
        await Run("apply sources use a closed allowlist with explicit unknown and omitted invalid values", SourceAllowlistAsync);
        await Run("blocked and failing analytics delivery cannot block or fail lockscreen apply", DeliveryIsolationAsync);
        await Run("lockscreen apply respects a saved analytics opt-out", OptOutAsync);
    }

    private static async Task ResultOutcomesAsync()
    {
        foreach (var source in Sources)
        {
            foreach (var workflow in Enum.GetValues<AnalyticsWorkflow>())
            {
                using var context = new TestContext(blockFirst: true);
                using var analytics = context.Create();
                Check(analytics.SetEnabled(true), "Analytics enabled for the fake transport.");
                analytics.Track(AnalyticsEvent.AppOpened);
                await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                var cases = new (LockscreenApplyResult Result, string Outcome)[]
                {
                    (CreateResult(success: true), "succeeded"),
                    (CreateResult(), "partial"),
                    (CreateResult(copied: false), "failed"),
                    (CreateResult(cancelled: true), "cancelled"),
                };
                foreach (var item in cases)
                {
                    var actual = await LockscreenApplyAnalytics.RunAsync(
                        analytics,
                        workflow,
                        source.Kind,
                        true,
                        () => Task.FromResult(item.Result)
                    );
                    Check(ReferenceEquals(actual, item.Result), "Tracking preserves the actual apply result.");
                }

                context.Handler.Release.TrySetResult();
                await analytics.ShutdownAsync();
                var requests = context
                    .Handler.Requests.Skip(1)
                    .Where(request => request.Root.GetProperty("event").GetString() != "$exception")
                    .ToArray();
                Check(requests.Length == cases.Length * 2, "Every accepted apply emits exactly one start and one completion.");
                var operationIds = new HashSet<Guid>();
                for (var index = 0; index < cases.Length; index++)
                {
                    var item = cases[index];
                    var started = requests[index * 2];
                    var completed = requests[index * 2 + 1];
                    AssertPair(started, completed, source.WireValue, workflow, item.Outcome);
                    Check(
                        operationIds.Add(started.Properties.GetProperty("operation_id").GetGuid()),
                        "Each apply has its own operation ID."
                    );
                    var properties = completed.Properties;
                    Check(properties.GetProperty("api_requested").GetBoolean(), "The requested API option is retained.");
                    Check(
                        properties.GetProperty("api_completed").GetBoolean() == item.Result.ApiCompleted,
                        "Actual API completion is retained."
                    );
                    Check(properties.GetProperty("target_count").GetInt32() == 2, "The target count reflects the result.");
                    Check(
                        properties.GetProperty("copied_count").GetInt32() == item.Result.Files.Count(file => file.Copied),
                        "The copied count reflects actual cache writes."
                    );
                    Check(
                        properties.GetProperty("verified_count").GetInt32() == item.Result.Files.Count(file => file.Verified),
                        "The verified count reflects actual verification."
                    );
                    if (item.Result.Cancelled)
                    {
                        Check(!properties.TryGetProperty("failed_count", out _), "Cancellation does not invent failed targets.");
                    }
                    else
                    {
                        Check(
                            properties.GetProperty("failed_count").GetInt32()
                                == item.Result.Files.Count(file => !file.Copied || !file.Verified),
                            "Unsuccessful targets retain the existing failure count."
                        );
                    }

                    Check(properties.GetProperty("gif_size_bytes").GetInt64() == 8192, "Source size remains available on completion.");
                    Check(properties.GetProperty("gif_width").GetInt32() == 640, "Source width remains available on completion.");
                    Check(properties.GetProperty("gif_height").GetInt32() == 480, "Source height remains available on completion.");
                }
            }
        }
    }

    private static async Task ExceptionOutcomesAsync()
    {
        foreach (var source in Sources)
        {
            foreach (var workflow in Enum.GetValues<AnalyticsWorkflow>())
            {
                using var context = new TestContext(blockFirst: true);
                using var analytics = context.Create();
                Check(analytics.SetEnabled(true), "Analytics enabled.");
                analytics.Track(AnalyticsEvent.AppOpened);
                await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                var errors = new (Exception Error, string Outcome, string Kind)[]
                {
                    (new UnauthorizedAccessException(PrivateDetail), "failed", "permission_denied"),
                    (new OperationCanceledException(PrivateDetail), "cancelled", "cancelled"),
                };
                foreach (var item in errors)
                {
                    await AssertOriginalExceptionAsync(
                        () =>
                            LockscreenApplyAnalytics.RunAsync(
                                analytics,
                                workflow,
                                source.Kind,
                                false,
                                () => Task.FromException<LockscreenApplyResult>(item.Error)
                            ),
                        item.Error
                    );
                }

                context.Handler.Release.TrySetResult();
                await analytics.ShutdownAsync();
                var errorsReported = context
                    .Handler.Requests.Where(request => request.Root.GetProperty("event").GetString() == "$exception")
                    .ToArray();
                Check(errorsReported.Length == 1, "One thrown failure is reported; cancellation is not an error tracking event.");
                var requests = context
                    .Handler.Requests.Skip(1)
                    .Where(request => request.Root.GetProperty("event").GetString() != "$exception")
                    .ToArray();
                Check(requests.Length == errors.Length * 2, "A thrown failure or cancellation emits exactly one terminal event.");
                for (var index = 0; index < errors.Length; index++)
                {
                    var completed = requests[index * 2 + 1];
                    AssertPair(requests[index * 2], completed, source.WireValue, workflow, errors[index].Outcome);
                    Check(!completed.Properties.GetProperty("api_requested").GetBoolean(), "False API options survive exceptions.");
                    Check(completed.Properties.GetProperty("error_kind").GetString() == errors[index].Kind, "Error categories stay typed.");
                    Check(
                        !completed.Properties.TryGetProperty("target_count", out _),
                        "A thrown exception does not invent target results."
                    );
                }
            }
        }
    }

    private static async Task ReturnedFailuresAsync()
    {
        foreach (var source in Sources)
        {
            foreach (var workflow in Enum.GetValues<AnalyticsWorkflow>())
            {
                using var context = new TestContext(blockFirst: true);
                using var analytics = context.Create();
                Check(analytics.SetEnabled(true), "Analytics enabled for the fake transport.");
                analytics.Track(AnalyticsEvent.AppOpened);
                await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                var cases = new (LockscreenApplyResult Result, string Outcome, bool ReportsError)[]
                {
                    (CreateResult(), "partial", true),
                    (CreateResult(copied: false), "failed", true),
                    (CreateResult(cancelled: true), "cancelled", false),
                    (CreateResult(success: true), "succeeded", false),
                };
                foreach (var item in cases)
                {
                    item.Result.FailureException = new IOException(PrivateDetail);
                    item.Result.Files[0].FailureException = item.Result.FailureException;
                    item.Result.Files[1].FailureException = new InvalidDataException(PrivateDetail);
                    var actual = await LockscreenApplyAnalytics.RunAsync(
                        analytics,
                        workflow,
                        source.Kind,
                        true,
                        () => Task.FromResult(item.Result)
                    );
                    Check(ReferenceEquals(actual, item.Result), "Reporting preserves returned apply results, including partial failures.");
                }

                context.Handler.Release.TrySetResult();
                await analytics.ShutdownAsync();
                var requests = context.Handler.Requests.Skip(1).ToArray();
                var semantic = requests.Where(request => request.Root.GetProperty("event").GetString() != "$exception").ToArray();
                var failures = requests.Where(request => request.Root.GetProperty("event").GetString() == "$exception").ToArray();
                Check(semantic.Length == cases.Length * 2, "Every operation retains exactly one start and completion.");
                Check(failures.Length == 2, "Failed and partial operations each report once, regardless of failed file count.");
                for (var index = 0; index < cases.Length; index++)
                {
                    var item = cases[index];
                    var started = semantic[index * 2];
                    var completed = semantic[index * 2 + 1];
                    AssertPair(started, completed, source.WireValue, workflow, item.Outcome);
                    var operationId = started.Properties.GetProperty("operation_id").GetGuid();
                    var matching = failures
                        .Where(request => request.Properties.GetProperty("operation_id").GetGuid() == operationId)
                        .ToArray();
                    Check(matching.Length == (item.ReportsError ? 1 : 0), "Only failed, noncancelled operations have an exception event.");
                    if (item.ReportsError)
                    {
                        Check(
                            matching[0].Properties.GetProperty("lockscreen_source").GetString() == source.WireValue,
                            "Error provenance retains the source."
                        );
                        Check(
                            matching[0].Properties.GetProperty("workflow").GetString()
                                == (workflow == AnalyticsWorkflow.Lockscreen ? "lockscreen" : "diagnostics"),
                            "Diagnostic errors remain separate from normal lockscreen errors."
                        );
                    }
                }
                Check(
                    requests.All(request => !request.Raw.Contains("private", StringComparison.OrdinalIgnoreCase)),
                    "No result strings, paths, hashes, or exception messages are captured."
                );
            }
        }
    }

    private static async Task SourceSnapshotAsync()
    {
        using var context = new TestContext(blockFirst: true);
        using var analytics = context.Create();
        Check(analytics.SetEnabled(true), "Analytics enabled.");
        analytics.Track(AnalyticsEvent.AppOpened);
        await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var selected = new LockscreenSource(PrivateDetail, LockscreenSourceKind.Video);
        var finish = new TaskCompletionSource<LockscreenApplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applying = LockscreenApplyAnalytics.RunAsync(analytics, AnalyticsWorkflow.Lockscreen, selected.Kind, true, () => finish.Task);
        Check(!applying.IsCompleted, "The apply remains pending until its own work completes.");
        selected = new LockscreenSource(PrivateDetail, LockscreenSourceKind.BundledGif);
        finish.SetResult(CreateResult(success: true));
        await applying;
        context.Handler.Release.TrySetResult();
        await analytics.ShutdownAsync();
        var requests = context.Handler.Requests.Skip(1).ToArray();
        Check(selected.Kind == LockscreenSourceKind.BundledGif, "The user selection changed during the pending operation.");
        Check(requests.Length == 2, "The pending operation emitted exactly one start and completion.");
        AssertPair(requests[0], requests[1], "video", AnalyticsWorkflow.Lockscreen, "succeeded");
    }

    private static async Task SourceAllowlistAsync()
    {
        using var context = new TestContext(blockFirst: true);
        using var analytics = context.Create();
        Check(analytics.SetEnabled(true), "Analytics enabled.");
        analytics.Track(AnalyticsEvent.AppOpened);
        await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        foreach (var source in new LockscreenSourceKind?[] { LockscreenSourceKind.Unknown, (LockscreenSourceKind)999, null })
        {
            analytics.Track(AnalyticsEvent.LockscreenApplyStarted, new AnalyticsProperties { LockscreenSource = source });
            analytics.Track(AnalyticsEvent.LockscreenApplyCompleted, new AnalyticsProperties { LockscreenSource = source });
        }

        context.Handler.Release.TrySetResult();
        await analytics.ShutdownAsync();
        var requests = context.Handler.Requests.Skip(1).ToArray();
        Check(requests.Length == 6, "All allowlist examples reached the fake transport.");
        Check(
            requests.Take(2).All(request => request.Properties.GetProperty("lockscreen_source").GetString() == "unknown"),
            "An explicitly unknown source remains measurable on both events."
        );
        Check(
            requests.Skip(2).All(request => !request.Properties.TryGetProperty("lockscreen_source", out _)),
            "Unrecognized enum values and absent metadata are omitted instead of serialized as arbitrary values."
        );
    }

    private static async Task DeliveryIsolationAsync()
    {
        foreach (var failFirst in new[] { false, true })
        {
            using var context = new TestContext(blockFirst: true, failFirst: failFirst);
            using var analytics = context.Create();
            Check(analytics.SetEnabled(true), "Analytics enabled.");
            var result = CreateResult(success: true);
            try
            {
                var actual = await LockscreenApplyAnalytics
                    .RunAsync(
                        analytics,
                        AnalyticsWorkflow.Lockscreen,
                        LockscreenSourceKind.UserGif,
                        true,
                        async () =>
                        {
                            await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                            return result;
                        }
                    )
                    .WaitAsync(TimeSpan.FromSeconds(2));
                Check(ReferenceEquals(actual, result), "A blocked transport does not delay or alter a successful apply.");
                Check(!context.Handler.Release.Task.IsCompleted, "Apply completed while analytics delivery was still blocked.");
                var error = new InvalidOperationException(PrivateDetail);
                await AssertOriginalExceptionAsync(
                    () =>
                        LockscreenApplyAnalytics
                            .RunAsync(
                                analytics,
                                AnalyticsWorkflow.Diagnostics,
                                LockscreenSourceKind.BundledGif,
                                false,
                                () => Task.FromException<LockscreenApplyResult>(error)
                            )
                            .WaitAsync(TimeSpan.FromSeconds(2)),
                    error
                );
            }
            finally
            {
                context.Handler.Release.TrySetResult();
            }

            await analytics.ShutdownAsync();
            var requests = context
                .Handler.Requests.Where(request => request.Root.GetProperty("event").GetString() != "$exception")
                .ToArray();
            Check(requests.Length == 4, "Delivery resumes for queued start/completion pairs even after a transport exception.");
            AssertPair(requests[0], requests[1], "user_gif", AnalyticsWorkflow.Lockscreen, "succeeded");
            AssertPair(requests[2], requests[3], "bundled_gif", AnalyticsWorkflow.Diagnostics, "failed");
        }
    }

    private static async Task OptOutAsync()
    {
        using var context = new TestContext();
        using var analytics = context.Create();
        Check(analytics.SetEnabled(false), "The opt-out is saved.");
        var result = CreateResult(success: true);
        var actual = await LockscreenApplyAnalytics.RunAsync(
            analytics,
            AnalyticsWorkflow.Lockscreen,
            LockscreenSourceKind.Video,
            true,
            () => Task.FromResult(result)
        );
        Check(ReferenceEquals(actual, result), "An opted-out apply still succeeds.");
        await analytics.ShutdownAsync();
        Check(context.Handler.Requests.IsEmpty, "Apply source tracking never overrides a saved opt-out.");
    }

    private static LockscreenApplyResult CreateResult(bool success = false, bool cancelled = false, bool copied = true) =>
        new()
        {
            Success = success,
            Cancelled = cancelled,
            ApiRequested = true,
            ApiCompleted = success,
            SourceSizeBytes = 8192,
            SourceWidth = 640,
            SourceHeight = 480,
            Error = PrivateDetail,
            Files =
            [
                new()
                {
                    Path = PrivateDetail,
                    Copied = copied,
                    Verified = copied,
                    Error = PrivateDetail,
                    Sha256 = PrivateDetail,
                },
                new()
                {
                    Path = PrivateDetail,
                    Copied = success,
                    Verified = success,
                    Error = PrivateDetail,
                },
            ],
        };

    private static void AssertPair(
        CapturedRequest started,
        CapturedRequest completed,
        string source,
        AnalyticsWorkflow workflow,
        string outcome
    )
    {
        Check(started.Root.GetProperty("event").GetString() == "lockscreen_apply_started", "The accepted operation emits a start first.");
        Check(completed.Root.GetProperty("event").GetString() == "lockscreen_apply_completed", "The terminal event follows its start.");
        var operationId = started.Properties.GetProperty("operation_id").GetGuid();
        Check(operationId != Guid.Empty, "Apply receives a nonempty random operation ID.");
        Check(completed.Properties.GetProperty("operation_id").GetGuid() == operationId, "Both events correlate to the same operation.");
        var workflowName = workflow == AnalyticsWorkflow.Lockscreen ? "lockscreen" : "diagnostics";
        foreach (var request in new[] { started, completed })
        {
            Check(request.Properties.GetProperty("lockscreen_source").GetString() == source, "Both events retain the source category.");
            Check(request.Properties.GetProperty("workflow").GetString() == workflowName, "Diagnostic and normal workflows stay separate.");
            Check(
                !request.Raw.Contains("private", StringComparison.OrdinalIgnoreCase),
                "Paths, messages, and file hashes never enter analytics."
            );
        }

        Check(!started.Properties.TryGetProperty("outcome", out _), "A start does not claim an outcome.");
        Check(completed.Properties.GetProperty("outcome").GetString() == outcome, "The completion reflects the actual apply outcome.");
        Check(completed.Properties.GetProperty("duration_ms").GetDouble() >= 0, "Completion retains a nonnegative elapsed duration.");
    }

    private static async Task AssertOriginalExceptionAsync(Func<Task<LockscreenApplyResult>> action, Exception expected)
    {
        try
        {
            await action();
        }
        catch (Exception actual)
        {
            Check(ReferenceEquals(actual, expected), "Tracking preserves the original exception, including cancellation.");
            return;
        }

        throw new InvalidOperationException("The failing apply unexpectedly completed without its original exception.");
    }

    private static async Task Run(string name, Func<Task> test)
    {
        await test();
        Console.WriteLine("PASS " + name);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
