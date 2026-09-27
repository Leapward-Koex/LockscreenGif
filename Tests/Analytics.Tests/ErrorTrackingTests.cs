using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using LockscreenGif.Models;
using LockscreenGif.Services;
using LockscreenGif.Services.Analytics;

internal static class ErrorTrackingTests
{
    private const string PrivateDetail = @"synthetic-private C:\Users\private-person\private-movie.mp4";

    public static async Task RunAsync()
    {
        await Run("failed operations emit a PostHog exception with their original operation metadata", SchemaAsync);
        await Run("error tracking never reads or serializes private exception content", PrivacyAsync);
        await Run("rethrows and known wrappers report an exception once while new failures remain visible", DeduplicationAsync);
        await Run("handled cancellation keeps its terminal event without creating an error issue", CancellationAsync);
        await Run("exception fingerprints group stable failures and distinguish stage, context, and native codes", FingerprintsAsync);
        await Run("error tracking preserves opt-out, delivery restrictions, and environment routing", RestrictionsAsync);
        await Run("blocked and throwing error transport cannot delay or replace an application failure", DeliveryIsolationAsync);
        await Run("exception delivery has a bounded shutdown when transport stalls", ShutdownAsync);
    }

    private static async Task SchemaAsync()
    {
        using var context = new TestContext(blockFirst: true);
        using var service = context.Create();
        await BlockDeliveryAsync(context, service);
        var operation = new AnalyticsProperties
        {
            OperationId = Guid.NewGuid(),
            Workflow = AnalyticsWorkflow.Lockscreen,
            LockscreenSource = LockscreenSourceKind.Video,
            DurationMs = 1550301,
            FailureStage = AnalyticsGenerationStage.EncodingGif,
            SourceFps = 30,
            TargetFps = 0,
        };
        var error = new IOException(PrivateDetail);
        service.TrackFailure(AnalyticsEvent.GifGenerationCompleted, error, operation);
        context.Handler.Release.TrySetResult();
        await service.ShutdownAsync();

        var requests = context.Handler.Requests.Skip(1).ToArray();
        Check(requests.Length == 2, "One failure emits its existing terminal event and one exception event.");
        var terminal = requests.Single(request => EventName(request) == "gif_generation_completed");
        var captured = requests.Single(request => EventName(request) == "$exception");
        Check(
            !terminal.Properties.TryGetProperty("error_context", out _),
            "Exception-only properties do not alter the usage event contract."
        );
        foreach (var request in requests)
        {
            var properties = request.Properties;
            Check(properties.GetProperty("operation_id").GetGuid() == operation.OperationId, "Both events join the same operation.");
            Check(properties.GetProperty("lockscreen_source").GetString() == "video", "Error context preserves media provenance.");
            Check(properties.GetProperty("workflow").GetString() == "lockscreen", "Normal and diagnostic workflows remain distinct.");
            Check(properties.GetProperty("outcome").GetString() == "failed", "The actual failure outcome is retained.");
            Check(properties.GetProperty("failure_stage").GetString() == "encoding_gif", "The failed stage is retained.");
            Check(properties.GetProperty("error_hresult").GetInt32() == error.HResult, "HRESULT stays numeric.");
            Check(properties.GetProperty("duration_ms").GetDouble() == operation.DurationMs, "Elapsed time is retained.");
            Check(
                properties.GetProperty("requested_fps_mode").GetString() == "all_source_frames",
                "All-source-frames selection is explicit."
            );
            Check(!properties.GetProperty("$process_person_profile").GetBoolean(), "Exception capture does not create person profiles.");
            Check(properties.GetProperty("$geoip_disable").GetBoolean(), "Exception capture does not enable GeoIP.");
            AssertPrivateDataAbsent(request);
        }

        Check(captured.Url == "https://eu.i.posthog.com/capture/", "Exception delivery uses the existing approved EU ingestion endpoint.");
        Check(captured.ContentType == "application/json", "The exception is captured as JSON.");
        Check(captured.Properties.GetProperty("error_context").GetString() == "gif_generation", "The known error boundary is explicit.");
        var exception = AssertExceptionSchema(captured, handled: true);
        Check(exception.GetProperty("type").GetString() == "IOException", "PostHog receives a recognizable allowlisted exception family.");
        var fingerprint = Fingerprint(captured);
        Check(fingerprint.Length == 64 && fingerprint.All(Uri.IsHexDigit), "The custom fingerprint is a stable 64-character hex string.");
        Check(
            terminal.Properties.GetProperty("distinct_id").GetString() == captured.Properties.GetProperty("distinct_id").GetString()
                && terminal.Properties.GetProperty("$session_id").GetString() == captured.Properties.GetProperty("$session_id").GetString(),
            "Error and usage events share the existing random installation and session identifiers."
        );
    }

    private static async Task PrivacyAsync()
    {
        using var context = new TestContext(blockFirst: true);
        using var service = context.Create();
        await BlockDeliveryAsync(context, service);
        var error = new FileNotFoundException(PrivateDetail, PrivateDetail)
        {
            Source = PrivateDetail,
            HelpLink = "https://private.invalid/secret",
        };
        error.Data["private-key"] = PrivateDetail;
        service.CaptureException(error, AnalyticsErrorContext.GifSelection);
        service.CaptureException(new PrivateRuntimeFailure(), AnalyticsErrorContext.MainAction);
        service.CaptureException(new InvalidOperationException(PrivateDetail, error), AnalyticsErrorContext.VideoLoad);
        context.Handler.Release.TrySetResult();
        await service.ShutdownAsync();

        var requests = context.Handler.Requests.Skip(1).ToArray();
        Check(requests.Length == 3, "All independently observed errors reach the fake transport without reading sensitive getters.");
        Check(
            AssertExceptionSchema(requests[0], true).GetProperty("type").GetString() == "FileNotFoundException",
            "Known families stay useful."
        );
        foreach (var request in requests)
        {
            AssertExceptionSchema(request, true);
            AssertPrivateDataAbsent(request);
            Check(
                !request.Raw.Contains(nameof(PrivateRuntimeFailure), StringComparison.Ordinal),
                "Unknown runtime class names are never sent."
            );
        }
    }

    private static async Task DeduplicationAsync()
    {
        using var context = new TestContext(blockFirst: true);
        using var service = context.Create();
        await BlockDeliveryAsync(context, service);
        var first = new IOException(PrivateDetail);
        service.TrackFailure(AnalyticsEvent.VideoLoadCompleted, first);
        service.CaptureException(new AggregateException(new TargetInvocationException(first)), AnalyticsErrorContext.MainAction);
        service.CaptureException(first, AnalyticsErrorContext.AppCrash);
        service.CaptureException(new IOException(PrivateDetail), AnalyticsErrorContext.VideoLoad);
        var wrappedFirst = new IOException(PrivateDetail);
        service.CaptureException(new TypeInitializationException(PrivateDetail, wrappedFirst), AnalyticsErrorContext.VideoLoad);
        service.CaptureException(wrappedFirst, AnalyticsErrorContext.MainAction);
        context.Handler.Release.TrySetResult();
        await service.ShutdownAsync();

        var requests = context.Handler.Requests.Skip(1).ToArray();
        Check(requests.Count(request => EventName(request) == "video_load_completed") == 1, "The operation still has its terminal result.");
        var errors = requests.Where(request => EventName(request) == "$exception").ToArray();
        Check(errors.Length == 3, "The same underlying exception is reported once regardless of wrapper order or later catch boundary.");
        Check(
            errors.All(request => request.Properties.GetProperty("error_context").GetString() == "video_load"),
            "The first useful boundary wins."
        );
        Check(
            errors.Select(Fingerprint).Distinct().Count() == 1,
            "Distinct instances of the same failure remain separate occurrences of one issue."
        );
    }

    private static async Task CancellationAsync()
    {
        using var context = new TestContext(blockFirst: true);
        using var service = context.Create();
        await BlockDeliveryAsync(context, service);
        service.TrackFailure(
            AnalyticsEvent.GifGenerationCompleted,
            new AggregateException(new TargetInvocationException(new OperationCanceledException(PrivateDetail))),
            new AnalyticsProperties { OperationId = Guid.NewGuid() }
        );
        service.CaptureException(new OperationCanceledException(PrivateDetail), AnalyticsErrorContext.VideoPreview);
        service.CaptureException(new OperationCanceledException(PrivateDetail), AnalyticsErrorContext.AppCrash, handled: false);
        context.Handler.Release.TrySetResult();
        await service.ShutdownAsync();

        var requests = context.Handler.Requests.Skip(1).ToArray();
        Check(requests.Length == 2, "Expected handled cancellation is suppressed while an unhandled exception remains visible.");
        var terminal = requests.Single(request => EventName(request) == "gif_generation_completed");
        Check(terminal.Properties.GetProperty("outcome").GetString() == "cancelled", "Cancellation retains its funnel outcome.");
        Check(!terminal.Properties.TryGetProperty("$exception_list", out _), "The terminal event remains a normal analytics event.");
        AssertExceptionSchema(requests.Single(request => EventName(request) == "$exception"), handled: false);
    }

    private static async Task FingerprintsAsync()
    {
        using var context = new TestContext(blockFirst: true);
        using var service = context.Create();
        await BlockDeliveryAsync(context, service);
        var properties = new AnalyticsProperties
        {
            OperationId = Guid.NewGuid(),
            LockscreenSource = LockscreenSourceKind.Video,
            DurationMs = 10,
            FailureStage = AnalyticsGenerationStage.EncodingGif,
        };
        const int hresult = unchecked((int)0x80004005);
        service.CaptureException(new IOException(PrivateDetail, hresult), AnalyticsErrorContext.GifGeneration, properties);
        service.CaptureException(
            new IOException("another private message", hresult),
            AnalyticsErrorContext.GifGeneration,
            properties with
            {
                OperationId = Guid.NewGuid(),
                LockscreenSource = LockscreenSourceKind.UserGif,
                DurationMs = 999,
            }
        );
        service.CaptureException(
            new IOException(PrivateDetail, hresult),
            AnalyticsErrorContext.GifGeneration,
            properties with
            {
                FailureStage = AnalyticsGenerationStage.ExtractingFrames,
            }
        );
        service.CaptureException(
            new IOException(PrivateDetail, unchecked((int)0x80070070)),
            AnalyticsErrorContext.GifGeneration,
            properties
        );
        service.CaptureException(new IOException(PrivateDetail, hresult), AnalyticsErrorContext.GifSave, properties);
        service.CaptureException(
            new MediaProcessingException(MediaProcessingComponent.Gifski, 9, PrivateDetail),
            AnalyticsErrorContext.GifGeneration,
            properties
        );
        service.CaptureException(
            new MediaProcessingException(MediaProcessingComponent.Gifski, 10, PrivateDetail),
            AnalyticsErrorContext.GifGeneration,
            properties
        );
        service.CaptureException(
            new MediaProcessingException(MediaProcessingComponent.Ffmpeg, 9, PrivateDetail),
            AnalyticsErrorContext.GifGeneration,
            properties
        );
        context.Handler.Release.TrySetResult();
        await service.ShutdownAsync();

        var requests = context.Handler.Requests.Skip(1).ToArray();
        Check(requests.Length == 8, "Every independent fingerprint example is captured.");
        Check(
            Fingerprint(requests[0]) == Fingerprint(requests[1]),
            "Message, random operation ID, source, and duration do not split an issue."
        );
        Check(
            requests.Skip(1).Select(Fingerprint).Distinct().Count() == 7,
            "Context, stage, HRESULT, native code, and component distinguish failures."
        );
        foreach (var request in requests)
        {
            AssertPrivateDataAbsent(request);
        }
    }

    private static async Task RestrictionsAsync()
    {
        foreach (var mode in new[] { "uninitialized", "opted_out", "delivery_disabled", "invalid_configuration" })
        {
            using var context = new TestContext();
            using var service = context.Create(
                allowSending: mode != "delivery_disabled",
                options: mode == "invalid_configuration"
                    ? new AnalyticsOptions { ProjectToken = "phc_test_only", Host = "https://private.invalid" }
                    : null
            );
            if (mode != "uninitialized")
            {
                service.SetEnabled(mode != "opted_out");
            }
            service.TrackFailure(AnalyticsEvent.GifSaveCompleted, new IOException(PrivateDetail));
            service.CaptureException(new IOException(PrivateDetail), AnalyticsErrorContext.AppCrash, handled: false);
            await service.ShutdownAsync();
            Check(context.Handler.Requests.IsEmpty, "No error event bypasses " + mode + ".");
        }

        foreach (var production in new[] { false, true })
        {
            using var context = new TestContext(blockFirst: true);
            using var service = context.Create(
                options: AnalyticsOptions.ForBuild(production, "phc_synthetic_prod", "phc_synthetic_dev", "https://eu.i.posthog.com")
            );
            await BlockDeliveryAsync(context, service);
            service.CaptureException(new IOException(PrivateDetail), (AnalyticsErrorContext)999);
            service.TrackFailure((AnalyticsEvent)999, new IOException(PrivateDetail));
            service.CaptureException(new IOException(PrivateDetail), AnalyticsErrorContext.GifSave);
            context.Handler.Release.TrySetResult();
            await service.ShutdownAsync();
            var requests = context.Handler.Requests.Skip(1).ToArray();
            Check(requests.Length == 1, "Unknown contexts and events fail closed.");
            Check(
                requests[0].Root.GetProperty("api_key").GetString() == (production ? "phc_synthetic_prod" : "phc_synthetic_dev"),
                "Errors use the selected environment token."
            );
            Check(
                requests[0].Properties.GetProperty("environment").GetString() == (production ? "production" : "development"),
                "Errors retain the typed environment marker."
            );
        }
    }

    private static async Task DeliveryIsolationAsync()
    {
        using var context = new TestContext(blockFirst: true, failFirst: true);
        using var service = context.Create();
        Check(service.SetEnabled(true), "Analytics enabled with a fake HTTP transport.");
        service.CaptureException(new IOException(PrivateDetail), AnalyticsErrorContext.GifSave);
        await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var expected = new InvalidOperationException(PrivateDetail);
        try
        {
            var elapsed = await Task.Run(() =>
                {
                    var timer = Stopwatch.StartNew();
                    try
                    {
                        throw expected;
                    }
                    catch (Exception actual)
                    {
                        service.TrackFailure(AnalyticsEvent.GifGenerationCompleted, actual);
                        Check(ReferenceEquals(actual, expected), "Error capture preserves the original application exception.");
                    }
                    return timer.Elapsed;
                })
                .WaitAsync(TimeSpan.FromSeconds(2));
            Check(elapsed < TimeSpan.FromMilliseconds(500), "Error capture does not await stalled delivery.");
            Check(!context.Handler.Release.Task.IsCompleted, "The application error was handled before transport was released.");
        }
        finally
        {
            context.Handler.Release.TrySetResult();
        }
        await service.ShutdownAsync();
        var requests = context.Handler.Requests.ToArray();
        Check(requests.Length == 3, "A failed exception request cannot stop queued terminal and exception events.");
        Check(
            requests.Count(request => EventName(request) == "$exception") == 2,
            "Only application errors are captured; transport errors do not recurse."
        );
        Check(
            requests.All(request => !request.Raw.Contains("transport-detail", StringComparison.Ordinal)),
            "Transport error content is not recaptured."
        );
    }

    private static async Task ShutdownAsync()
    {
        using var context = new TestContext(blockFirst: true);
        using var service = context.Create();
        Check(service.SetEnabled(true), "Analytics enabled with a fake HTTP transport.");
        service.CaptureException(new IOException(PrivateDetail), AnalyticsErrorContext.AppShutdown);
        await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        service.CaptureException(new IOException(PrivateDetail), AnalyticsErrorContext.AppShutdown);
        var timer = Stopwatch.StartNew();
        await service.ShutdownAsync();
        Check(timer.Elapsed < TimeSpan.FromSeconds(2), "Stalled exception delivery cannot hold application shutdown.");
        await context.Handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Check(context.Handler.Requests.Count == 1, "Expired shutdown discards queued exception events.");
    }

    private static async Task BlockDeliveryAsync(TestContext context, AnalyticsService service)
    {
        Check(service.SetEnabled(true), "Analytics enabled with a fake HTTP transport.");
        service.Track(AnalyticsEvent.AppOpened);
        await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static JsonElement AssertExceptionSchema(CapturedRequest request, bool handled)
    {
        Check(EventName(request) == "$exception", "PostHog receives its reserved exception event name.");
        var list = request.Properties.GetProperty("$exception_list");
        Check(
            list.ValueKind == JsonValueKind.Array && list.GetArrayLength() == 1,
            "The exception list contains one privacy-safe error summary."
        );
        var exception = list[0];
        var expected = new HashSet<string> { "type", "value", "mechanism" };
        Check(
            expected.SetEquals(exception.EnumerateObject().Select(property => property.Name)),
            "Exception entries contain no stack, paths, or incidental object fields."
        );
        Check(!string.IsNullOrWhiteSpace(exception.GetProperty("type").GetString()), "The exception type is populated.");
        var value = exception.GetProperty("value").GetString();
        Check(!string.IsNullOrWhiteSpace(value) && value.Length <= 256, "The issue description is a bounded generated summary.");
        var mechanism = exception.GetProperty("mechanism");
        Check(mechanism.GetProperty("handled").GetBoolean() == handled, "Handled state reflects the actual boundary.");
        Check(!mechanism.GetProperty("synthetic").GetBoolean(), "The event represents an observed exception.");
        Check(mechanism.GetProperty("type").GetString() == "manual", "Capture uses the manual PostHog mechanism.");
        return exception;
    }

    private static void AssertPrivateDataAbsent(CapturedRequest request) =>
        Check(
            !request.Raw.Contains("private", StringComparison.OrdinalIgnoreCase)
                && !request.Raw.Contains("stacktrace", StringComparison.OrdinalIgnoreCase)
                && !request.Raw.Contains("filename", StringComparison.OrdinalIgnoreCase),
            "Exception messages, filenames, paths, Data, HelpLink, Source, and stack traces never enter analytics."
        );

    private static string EventName(CapturedRequest request) => request.Root.GetProperty("event").GetString()!;

    private static string Fingerprint(CapturedRequest request) => request.Properties.GetProperty("$exception_fingerprint").GetString()!;

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

    private sealed class PrivateRuntimeFailure : Exception
    {
        public override string Message => throw new InvalidOperationException("Private exception messages must not be inspected.");
        public override string? StackTrace => throw new InvalidOperationException("Private stack traces must not be inspected.");
        public override IDictionary Data => throw new InvalidOperationException("Private exception data must not be inspected.");

        public override string ToString() => throw new InvalidOperationException("Private exception text must not be inspected.");
    }
}
