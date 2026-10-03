using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using LockscreenGif.Services;
using LockscreenGif.Services.Analytics;

internal static class MediaLoadErrorTests
{
    private const string PrivateDetail = @"synthetic-private C:\Users\private-person\private-movie.mp4";

    public static async Task RunAsync()
    {
        await ErrorCodesAsync();
        Console.WriteLine("PASS media error codes classify exact HRESULTs without reading private details");
        await MediaContextAsync();
        Console.WriteLine("PASS media load stages and recovery observations are closed, bounded, and retained on exceptions");
        await CancellationAsync();
        Console.WriteLine("PASS media recovery context preserves cancellation without creating an error issue");
        Guidance();
        Console.WriteLine("PASS media guidance separates decoder recovery from policy, permissions, and cancellation");
    }

    private static async Task ErrorCodesAsync()
    {
        var cases = new (Exception Error, AnalyticsEvent Event, string Kind, int? NativeCode)[]
        {
            (new COMException(PrivateDetail, -2147467259), AnalyticsEvent.VideoLoadCompleted, "native_failure", null),
            (new COMException(PrivateDetail, -2147467259), AnalyticsEvent.GifSelected, "native_failure", null),
            (new IOException(PrivateDetail, -2147020345), AnalyticsEvent.GifGenerationCompleted, "security_policy_blocked", 4551),
            (new COMException(PrivateDetail, -1072868846), AnalyticsEvent.VideoLoadCompleted, "codec_missing", null),
            (new Win32Exception(4551, PrivateDetail), AnalyticsEvent.GifGenerationCompleted, "security_policy_blocked", 4551),
            (new Win32Exception(1260, PrivateDetail), AnalyticsEvent.GifGenerationCompleted, "security_policy_blocked", 1260),
            (new Win32Exception(577, PrivateDetail), AnalyticsEvent.GifGenerationCompleted, "security_policy_blocked", 577),
            (
                new COMException(PrivateDetail, unchecked((int)0x800704EC)),
                AnalyticsEvent.GifGenerationCompleted,
                "security_policy_blocked",
                1260
            ),
            (
                new IOException(PrivateDetail, unchecked((int)0x80070241)),
                AnalyticsEvent.GifGenerationCompleted,
                "security_policy_blocked",
                577
            ),
            (new COMException(PrivateDetail, unchecked((int)0x800711C7)), AnalyticsEvent.GifSelected, "security_policy_blocked", 4551),
            (new IOException(PrivateDetail, unchecked((int)0x80070020)), AnalyticsEvent.GifGenerationCompleted, "io", 32),
            (new COMException(PrivateDetail, unchecked((int)0x800D11C7)), AnalyticsEvent.VideoLoadCompleted, "native_failure", null),
        };
        using var context = new TestContext(blockFirst: true);
        using var service = context.Create();
        await BlockDeliveryAsync(context, service);
        var operationId = Guid.NewGuid();
        foreach (var item in cases)
        {
            item.Error.Data["private-key"] = PrivateDetail;
            service.TrackFailure(item.Event, item.Error, new AnalyticsProperties { OperationId = operationId });
        }
        context.Handler.Release.TrySetResult();
        await service.ShutdownAsync();
        var requests = context.Handler.Requests.Skip(1).ToArray();
        Check(requests.Length == cases.Length * 2, "Every failure has a terminal event and companion error.");
        for (var index = 0; index < cases.Length; index++)
        {
            foreach (var request in requests.Skip(index * 2).Take(2))
            {
                var properties = request.Properties;
                Check(properties.GetProperty("error_kind").GetString() == cases[index].Kind, "Classification uses numeric evidence.");
                Check(
                    properties.GetProperty("error_hresult").GetInt32() == cases[index].Error.HResult,
                    "The original HRESULT is retained."
                );
                Check(properties.GetProperty("operation_id").GetGuid() == operationId, "Failure context retains operation correlation.");
                Check(!properties.TryGetProperty("error_component", out _), "Windows errors do not imply an FFmpeg or Gifski component.");
                if (cases[index].NativeCode is { } code)
                {
                    Check(properties.GetProperty("native_error_code").GetInt32() == code, "The actual Win32 code is retained.");
                }
                else
                {
                    Check(!properties.TryGetProperty("native_error_code", out _), "Other facilities are not interpreted as Win32 codes.");
                }
                Check(!request.Raw.Contains("private", StringComparison.OrdinalIgnoreCase), "No exception text or data is sent.");
            }
        }
    }

    private static async Task MediaContextAsync()
    {
        var stages = new (AnalyticsMediaLoadStage Stage, string Value)[]
        {
            (AnalyticsMediaLoadStage.PickingFile, "picking_file"),
            (AnalyticsMediaLoadStage.ReadingMetadata, "reading_metadata"),
            (AnalyticsMediaLoadStage.IndexingFrames, "indexing_frames"),
            (AnalyticsMediaLoadStage.OpeningPreview, "opening_preview"),
            (AnalyticsMediaLoadStage.OpeningFile, "opening_file"),
            (AnalyticsMediaLoadStage.DecodingImage, "decoding_image"),
            (AnalyticsMediaLoadStage.Completing, "completing"),
            (AnalyticsMediaLoadStage.PlayingPreview, "playing_preview"),
            (AnalyticsMediaLoadStage.ReadingFallbackMetadata, "reading_fallback_metadata"),
        };
        using var context = new TestContext(blockFirst: true);
        using var service = context.Create();
        await BlockDeliveryAsync(context, service);
        var operationId = Guid.NewGuid();
        foreach (var stage in stages)
        {
            service.CaptureException(
                new COMException(PrivateDetail, -2147467259),
                AnalyticsErrorContext.VideoLoad,
                new AnalyticsProperties
                {
                    OperationId = operationId,
                    MediaLoadStage = stage.Stage,
                    MetadataFallbackUsed = true,
                    PlaybackAvailable = false,
                }
            );
        }
        service.CaptureException(new COMException(PrivateDetail, -2147467259), AnalyticsErrorContext.VideoLoad);
        service.CaptureException(
            new COMException(PrivateDetail, -2147467259),
            AnalyticsErrorContext.VideoLoad,
            new AnalyticsProperties { MediaLoadStage = (AnalyticsMediaLoadStage)999 }
        );
        service.CaptureException(
            new COMException(PrivateDetail, -2147467259),
            AnalyticsErrorContext.VideoLoad,
            new AnalyticsProperties
            {
                OperationId = Guid.NewGuid(),
                MediaLoadStage = AnalyticsMediaLoadStage.ReadingMetadata,
                MetadataFallbackUsed = false,
                PlaybackAvailable = true,
            }
        );
        service.Track(
            AnalyticsEvent.VideoLoadCompleted,
            new AnalyticsProperties
            {
                Outcome = AnalyticsOutcome.Succeeded,
                MediaLoadStage = AnalyticsMediaLoadStage.Completing,
                MetadataFallbackUsed = false,
                PlaybackAvailable = true,
            }
        );
        service.Track(AnalyticsEvent.GifSelected, new AnalyticsProperties { ErrorKind = (AnalyticsErrorKind)999 });
        context.Handler.Release.TrySetResult();
        await service.ShutdownAsync();
        var requests = context.Handler.Requests.Skip(1).ToArray();
        Check(requests.Length == stages.Length + 5, "Every media observation reached the fake transport.");
        for (var index = 0; index < stages.Length; index++)
        {
            var properties = requests[index].Properties;
            Check(properties.GetProperty("media_load_stage").GetString() == stages[index].Value, "Stage serialization is explicit.");
            Check(properties.GetProperty("metadata_fallback_used").GetBoolean(), "Metadata recovery survives failure enrichment.");
            Check(!properties.GetProperty("playback_available").GetBoolean(), "A false capability remains a recorded observation.");
            Check(properties.GetProperty("operation_id").GetGuid() == operationId, "CaptureException retains the operation join.");
        }
        foreach (var request in requests.Skip(stages.Length).Take(2))
        {
            Check(!request.Properties.TryGetProperty("media_load_stage", out _), "Missing and invalid stages are omitted.");
            Check(!request.Properties.TryGetProperty("metadata_fallback_used", out _), "Unknown recovery state is omitted.");
            Check(!request.Properties.TryGetProperty("playback_available", out _), "Unknown playback state is omitted.");
        }
        Check(
            Fingerprint(requests[stages.Length]) == Fingerprint(requests[stages.Length + 1]),
            "An invalid stage cannot split the existing issue fingerprint."
        );
        Check(
            Fingerprint(requests[1]) == Fingerprint(requests[stages.Length + 2]),
            "Recovery flags and operation IDs do not split error issues."
        );
        Check(
            requests.Take(stages.Length + 1).Select(Fingerprint).Distinct().Count() == stages.Length + 1,
            "Known load stages distinguish otherwise identical failures."
        );
        var success = requests[stages.Length + 3].Properties;
        Check(success.GetProperty("outcome").GetString() == "succeeded", "Recovery observations also describe successful loads.");
        Check(!success.GetProperty("metadata_fallback_used").GetBoolean(), "False metadata recovery is serialized.");
        Check(success.GetProperty("playback_available").GetBoolean(), "Available playback is serialized.");
        Check(!requests[stages.Length + 4].Properties.TryGetProperty("error_kind", out _), "Invalid error enum values fail closed.");
        Check(
            requests.All(request => !request.Raw.Contains("private", StringComparison.OrdinalIgnoreCase)),
            "Media context sends no private text."
        );
    }

    private static async Task CancellationAsync()
    {
        using var context = new TestContext(blockFirst: true);
        using var service = context.Create();
        await BlockDeliveryAsync(context, service);
        service.TrackFailure(
            AnalyticsEvent.VideoLoadCompleted,
            new TargetInvocationException(new OperationCanceledException(PrivateDetail)),
            new AnalyticsProperties { MediaLoadStage = AnalyticsMediaLoadStage.IndexingFrames, MetadataFallbackUsed = true }
        );
        context.Handler.Release.TrySetResult();
        await service.ShutdownAsync();
        var requests = context.Handler.Requests.Skip(1).ToArray();
        Check(requests.Length == 1, "Handled cancellation keeps only the terminal event.");
        Check(requests[0].Properties.GetProperty("outcome").GetString() == "cancelled", "Cancellation classification is unchanged.");
        Check(
            requests[0].Properties.GetProperty("media_load_stage").GetString() == "indexing_frames",
            "Cancellation retains the observed stage."
        );
    }

    private static void Guidance()
    {
        foreach (
            var error in new Exception[]
            {
                new COMException(PrivateDetail, -2147467259),
                new COMException(PrivateDetail, -1072868846),
                new InvalidDataException(PrivateDetail),
                new TimeoutException(PrivateDetail),
            }
        )
        {
            Check(MediaFailureGuidance.CanUseVideoFallback(error), "Windows decoder failures can use the alternative decoder.");
        }
        foreach (
            var error in new Exception[]
            {
                new IOException(PrivateDetail, -2147020345),
                new COMException(PrivateDetail, -2147020345),
                new Win32Exception(4551, PrivateDetail),
                new UnauthorizedAccessException(PrivateDetail),
                new COMException(PrivateDetail, unchecked((int)0x80070005)),
                new OperationCanceledException(PrivateDetail),
                new IOException(PrivateDetail),
            }
        )
        {
            Check(!MediaFailureGuidance.CanUseVideoFallback(error), "Access failures and cancellation never trigger another decoder.");
        }
        foreach (
            var error in new Exception[]
            {
                new IOException(PrivateDetail, -2147020345),
                new COMException(PrivateDetail, -1072868846),
                new UnauthorizedAccessException(PrivateDetail),
                new IOException(PrivateDetail, unchecked((int)0x80070070)),
                new OutOfMemoryException(PrivateDetail),
            }
        )
        {
            var message = MediaFailureGuidance.Message(error, "Generic failure.");
            Check(
                message != "Generic failure." && !message.Contains("private", StringComparison.OrdinalIgnoreCase),
                "Known failures have safe, actionable guidance."
            );
        }
        Check(
            MediaFailureGuidance.Message(new COMException(PrivateDetail, -2147467259), "Generic failure.") == "Generic failure.",
            "E_FAIL does not invent a specific cause."
        );
    }

    private static async Task BlockDeliveryAsync(TestContext context, AnalyticsService service)
    {
        Check(service.SetEnabled(true), "Analytics enabled only for fake transport.");
        service.Track(AnalyticsEvent.AppOpened);
        await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static string Fingerprint(CapturedRequest request) => request.Properties.GetProperty("$exception_fingerprint").GetString()!;

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
