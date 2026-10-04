using System.Diagnostics;
using LockscreenGif.Services.Analytics;

internal static class VideoIndexingTests
{
    private static readonly string[] IndexFields =
    [
        "hardware_decoding_requested",
        "index_decoder",
        "hardware_decoding_fallback_used",
        "indexing_duration_ms",
    ];

    public static async Task RunAsync()
    {
        await ObservationsAsync();
        Console.WriteLine("PASS video indexing distinguishes hardware preference, observed decoder, and CPU recovery");
        await BoundsAsync();
        Console.WriteLine("PASS video indexing analytics omit unknown states and invalid enum or duration values");
        await RecoveryContextAsync();
        Console.WriteLine("PASS video indexing observations survive later failure and cancellation without private details");
        await OptionalDeliveryAsync();
        Console.WriteLine("PASS video indexing analytics preserve opt-out and never wait for blocked transport");
    }

    private static async Task ObservationsAsync()
    {
        var cases = new (bool Requested, AnalyticsVideoIndexDecoder Decoder, bool Fallback, string Name)[]
        {
            (false, AnalyticsVideoIndexDecoder.Cpu, false, "cpu"),
            (true, AnalyticsVideoIndexDecoder.Cpu, false, "cpu"),
            (true, AnalyticsVideoIndexDecoder.D3D11, false, "d3d11"),
            (true, AnalyticsVideoIndexDecoder.Cpu, true, "cpu"),
        };
        using var context = new TestContext(blockFirst: true);
        using var service = context.Create();
        await BlockDeliveryAsync(context, service);
        var operationId = Guid.NewGuid();
        service.Track(
            AnalyticsEvent.VideoLoadStarted,
            new AnalyticsProperties { OperationId = operationId, HardwareDecodingRequested = true }
        );
        foreach (var item in cases)
        {
            service.Track(
                AnalyticsEvent.VideoLoadCompleted,
                new AnalyticsProperties
                {
                    OperationId = operationId,
                    Outcome = AnalyticsOutcome.Succeeded,
                    HardwareDecodingRequested = item.Requested,
                    IndexDecoder = item.Decoder,
                    HardwareDecodingFallbackUsed = item.Fallback,
                    IndexingDurationMs = 10341.6,
                }
            );
        }
        context.Handler.Release.TrySetResult();
        await service.ShutdownAsync();
        var requests = context.Handler.Requests.Skip(1).ToArray();
        Check(requests.Length == cases.Length + 1, "All observations reach only the fake transport.");
        var started = requests[0].Properties;
        Check(started.GetProperty("hardware_decoding_requested").GetBoolean(), "Start records the saved preference snapshot.");
        Check(
            IndexFields.Skip(1).All(field => !started.TryGetProperty(field, out _)),
            "Starting a load does not invent decoder, recovery, or timing observations."
        );
        for (var index = 0; index < cases.Length; index++)
        {
            var properties = requests[index + 1].Properties;
            Check(properties.GetProperty("operation_id").GetGuid() == operationId, "Start and completion retain their operation join.");
            Check(
                properties.GetProperty("hardware_decoding_requested").GetBoolean() == cases[index].Requested,
                "The requested preference is independent of the observed decoder."
            );
            Check(properties.GetProperty("index_decoder").GetString() == cases[index].Name, "Decoder values use the closed allowlist.");
            Check(
                properties.GetProperty("hardware_decoding_fallback_used").GetBoolean() == cases[index].Fallback,
                "CPU indexing while discovery is pending differs from an attempted hardware recovery."
            );
            Check(properties.GetProperty("indexing_duration_ms").GetDouble() == 10342, "Indexing duration is rounded to milliseconds.");
            Check(
                properties.EnumerateObject().Count() == 14,
                "Only common fields, outcome, operation ID, and the four approved observations are sent."
            );
        }
    }

    private static async Task BoundsAsync()
    {
        var durations = new double?[] { null, double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1, 86400001, 0, 86400000 };
        using var context = new TestContext(blockFirst: true);
        using var service = context.Create();
        await BlockDeliveryAsync(context, service);
        service.Track(AnalyticsEvent.VideoLoadCompleted, new AnalyticsProperties());
        foreach (var duration in durations)
        {
            service.Track(
                AnalyticsEvent.VideoLoadCompleted,
                new AnalyticsProperties { IndexDecoder = (AnalyticsVideoIndexDecoder)999, IndexingDurationMs = duration }
            );
        }
        context.Handler.Release.TrySetResult();
        await service.ShutdownAsync();
        var requests = context.Handler.Requests.Skip(1).ToArray();
        Check(IndexFields.All(field => !requests[0].Properties.TryGetProperty(field, out _)), "Missing observations remain unknown.");
        for (var index = 0; index < durations.Length; index++)
        {
            var properties = requests[index + 1].Properties;
            Check(!properties.TryGetProperty("index_decoder", out _), "An invalid enum never becomes a decoder identifier.");
            if (durations[index] is >= 0 and <= 86400000)
            {
                Check(
                    properties.GetProperty("indexing_duration_ms").GetDouble() == durations[index],
                    "Zero and the maximum permitted duration are retained."
                );
            }
            else
            {
                Check(!properties.TryGetProperty("indexing_duration_ms", out _), "Unbounded or non-finite durations are omitted.");
            }
        }
    }

    private static async Task RecoveryContextAsync()
    {
        using var context = new TestContext(blockFirst: true);
        using var service = context.Create();
        await BlockDeliveryAsync(context, service);
        var values = new AnalyticsProperties
        {
            OperationId = Guid.NewGuid(),
            MediaLoadStage = AnalyticsMediaLoadStage.OpeningPreview,
            HardwareDecodingRequested = true,
            IndexDecoder = AnalyticsVideoIndexDecoder.Cpu,
            HardwareDecodingFallbackUsed = true,
            IndexingDurationMs = 21500,
        };
        var failure = new InvalidOperationException(@"synthetic-private C:\Users\private\video.mkv GPU private-adapter-name");
        failure.Data["adapter"] = "private-adapter-name";
        service.TrackFailure(AnalyticsEvent.VideoLoadCompleted, failure, values);
        service.TrackFailure(AnalyticsEvent.VideoLoadCompleted, new OperationCanceledException("private"), values);
        service.TrackFailure(
            AnalyticsEvent.VideoLoadCompleted,
            new OperationCanceledException("private"),
            new AnalyticsProperties
            {
                MediaLoadStage = AnalyticsMediaLoadStage.IndexingFrames,
                HardwareDecodingRequested = true,
                IndexingDurationMs = 175,
            }
        );
        context.Handler.Release.TrySetResult();
        await service.ShutdownAsync();
        var requests = context.Handler.Requests.Skip(1).ToArray();
        Check(requests.Length == 4, "Failure reports its companion issue; cancellation only reports a terminal outcome.");
        foreach (var request in requests.Take(3))
        {
            var properties = request.Properties;
            Check(properties.GetProperty("operation_id").GetGuid() == values.OperationId, "Enrichment preserves operation correlation.");
            Check(properties.GetProperty("hardware_decoding_requested").GetBoolean(), "Enrichment preserves the requested preference.");
            Check(properties.GetProperty("index_decoder").GetString() == "cpu", "Later failure retains the completed index decoder.");
            Check(properties.GetProperty("hardware_decoding_fallback_used").GetBoolean(), "Later failure retains the completed recovery.");
            Check(properties.GetProperty("indexing_duration_ms").GetDouble() == 21500, "Later failure retains complete indexing time.");
            Check(
                !request.Raw.Contains("private", StringComparison.OrdinalIgnoreCase),
                "No media path, GPU identifier, or error text is sent."
            );
        }
        Check(requests[^1].Properties.GetProperty("outcome").GetString() == "cancelled", "Cancellation remains a separate outcome.");
        var unfinished = requests[^1].Properties;
        Check(unfinished.GetProperty("indexing_duration_ms").GetDouble() == 175, "Cancelled indexing retains its elapsed attempt time.");
        Check(
            !unfinished.TryGetProperty("index_decoder", out _) && !unfinished.TryGetProperty("hardware_decoding_fallback_used", out _),
            "Interrupted indexing does not claim a completed decoder or recovered result."
        );
    }

    private static async Task OptionalDeliveryAsync()
    {
        var values = new AnalyticsProperties
        {
            HardwareDecodingRequested = true,
            IndexDecoder = AnalyticsVideoIndexDecoder.D3D11,
            HardwareDecodingFallbackUsed = false,
            IndexingDurationMs = 50,
        };
        using (var context = new TestContext())
        {
            using var service = context.Create();
            Check(service.SetEnabled(false), "The opt-out preference is saved.");
            service.Track(AnalyticsEvent.VideoLoadCompleted, values);
            await service.ShutdownAsync();
            Check(context.Handler.Requests.IsEmpty, "Indexing observations never override opt-out.");
        }
        using (var context = new TestContext(blockFirst: true))
        {
            using var service = context.Create();
            await BlockDeliveryAsync(context, service);
            var clock = Stopwatch.StartNew();
            service.Track(AnalyticsEvent.VideoLoadCompleted, values);
            Check(clock.Elapsed < TimeSpan.FromSeconds(1), "A blocked transport does not delay the caller.");
            Check(!context.Handler.Release.Task.IsCompleted, "Delivery remains blocked independently of capture.");
            context.Handler.Release.TrySetResult();
            await service.ShutdownAsync();
            Check(context.Handler.Requests.Count == 2, "The deferred observation is sent when fake delivery resumes.");
        }
    }

    private static async Task BlockDeliveryAsync(TestContext context, AnalyticsService service)
    {
        Check(service.SetEnabled(true), "Collection is enabled only with a fake transport.");
        service.Track(AnalyticsEvent.AppOpened);
        await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
