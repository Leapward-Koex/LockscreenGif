using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using LockscreenGif.Services;
using LockscreenGif.Services.Analytics;

internal static class GenerationErrorTests
{
    private const string PrivateDetail = @"synthetic-private C:\Users\private-person\private-movie.mp4";

    public static async Task RunAsync()
    {
        await Run("generation failures report allowlisted exception families without private text", ErrorFamiliesAsync);
        await Run("native generation failures retain component and numeric error codes", NativeErrorsAsync);
        await Run("known single wrappers preserve cancellation without guessing through unrelated failures", WrappedErrorsAsync);
        await Run("generation stages, source FPS, frame counts, and durations have bounded payloads", GenerationPropertiesAsync);
        await Run("generation failure capture remains prompt during blocked and failed delivery", BlockedDeliveryAsync);
    }

    private static async Task ErrorFamiliesAsync()
    {
        var cases = new (Exception Error, AnalyticsErrorKind Kind, string ExceptionType)[]
        {
            (new OutOfMemoryException(PrivateDetail), AnalyticsErrorKind.OutOfMemory, "out_of_memory"),
            (new DllNotFoundException(PrivateDetail), AnalyticsErrorKind.DependencyMissing, "dll_not_found"),
            (new EntryPointNotFoundException(PrivateDetail), AnalyticsErrorKind.DependencyMissing, "entry_point_not_found"),
            (new BadImageFormatException(PrivateDetail), AnalyticsErrorKind.DependencyIncompatible, "bad_image_format"),
            (new InvalidOperationException(PrivateDetail), AnalyticsErrorKind.InvalidState, "invalid_operation"),
            (new ArgumentException(PrivateDetail), AnalyticsErrorKind.InvalidArgument, "argument"),
            (new FileNotFoundException(PrivateDetail, PrivateDetail), AnalyticsErrorKind.Io, "file_not_found"),
            (new DirectoryNotFoundException(PrivateDetail), AnalyticsErrorKind.Io, "directory_not_found"),
            (new IOException(PrivateDetail), AnalyticsErrorKind.Io, "io"),
            (new IOException(PrivateDetail, unchecked((int)0x80070070)), AnalyticsErrorKind.DiskFull, "io"),
            (new UnauthorizedAccessException(PrivateDetail), AnalyticsErrorKind.PermissionDenied, "unauthorized_access"),
            (new SecurityException(PrivateDetail), AnalyticsErrorKind.PermissionDenied, "security"),
            (new InvalidDataException(PrivateDetail), AnalyticsErrorKind.InvalidMedia, "invalid_data"),
            (new FormatException(PrivateDetail), AnalyticsErrorKind.InvalidMedia, "format"),
            (new TimeoutException(PrivateDetail), AnalyticsErrorKind.Timeout, "timeout"),
            (new COMException(PrivateDetail, unchecked((int)0x80004005)), AnalyticsErrorKind.NativeFailure, "com"),
            (new PrivateMediaException(PrivateDetail), AnalyticsErrorKind.Other, "other"),
        };
        using var context = new TestContext(blockFirst: true);
        using var service = context.Create();
        Check(service.SetEnabled(true), "Enabled.");
        service.Track(AnalyticsEvent.AppOpened);
        await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        foreach (var item in cases)
        {
            Check(AnalyticsProperties.ClassifyError(item.Error) == item.Kind, "The error category reflects the exception family.");
            service.Track(AnalyticsEvent.GifGenerationCompleted, new AnalyticsProperties().WithFailure(item.Error));
        }

        context.Handler.Release.TrySetResult();
        await service.ShutdownAsync();
        var requests = context.Handler.Requests.Skip(1).ToArray();
        Check(requests.Length == cases.Length, "Every synthetic failure reached the fake transport.");
        for (var index = 0; index < cases.Length; index++)
        {
            var properties = requests[index].Properties;
            Check(properties.GetProperty("outcome").GetString() == "failed", "Actual failures retain the failed outcome.");
            Check(
                properties.GetProperty("exception_type").GetString() == cases[index].ExceptionType,
                "Exception families are allowlisted."
            );
            Check(properties.GetProperty("error_hresult").GetInt32() == cases[index].Error.HResult, "The numeric HRESULT is preserved.");
            Check(!properties.TryGetProperty("native_error_code", out _), "Managed failures do not invent native error codes.");
            Check(!properties.TryGetProperty("error_component", out _), "Managed failures do not invent media components.");
            Check(
                !requests[index].Raw.Contains("private", StringComparison.OrdinalIgnoreCase),
                "Messages and paths never enter analytics."
            );
            Check(
                !requests[index].Raw.Contains(nameof(PrivateMediaException), StringComparison.Ordinal),
                "Arbitrary class names are omitted."
            );
        }
    }

    private static async Task NativeErrorsAsync()
    {
        var cases = new (Exception Error, AnalyticsErrorKind Kind, int Code, string? Component)[]
        {
            (new Win32Exception(5, PrivateDetail), AnalyticsErrorKind.PermissionDenied, 5, null),
            (new Win32Exception(112, PrivateDetail), AnalyticsErrorKind.DiskFull, 112, null),
            (
                new MediaProcessingException(MediaProcessingComponent.Ffmpeg, 137, PrivateDetail),
                AnalyticsErrorKind.NativeFailure,
                137,
                "ffmpeg"
            ),
            (new MediaProcessingException(MediaProcessingComponent.Gifski, 2, PrivateDetail), AnalyticsErrorKind.InvalidState, 2, "gifski"),
            (
                new MediaProcessingException(MediaProcessingComponent.Gifski, 7, PrivateDetail),
                AnalyticsErrorKind.PermissionDenied,
                7,
                "gifski"
            ),
            (new MediaProcessingException(MediaProcessingComponent.Gifski, 9, PrivateDetail), AnalyticsErrorKind.InvalidMedia, 9, "gifski"),
            (new MediaProcessingException(MediaProcessingComponent.Gifski, 10, PrivateDetail), AnalyticsErrorKind.Timeout, 10, "gifski"),
            (
                new MediaProcessingException(MediaProcessingComponent.Gifski, 12345, PrivateDetail),
                AnalyticsErrorKind.NativeFailure,
                12345,
                "gifski"
            ),
        };
        using var context = new TestContext(blockFirst: true);
        using var service = context.Create();
        Check(service.SetEnabled(true), "Enabled.");
        service.Track(AnalyticsEvent.AppOpened);
        await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        foreach (var item in cases)
        {
            Check(AnalyticsProperties.ClassifyError(item.Error) == item.Kind, "Native category comes from the numeric error code.");
            service.Track(AnalyticsEvent.GifGenerationCompleted, new AnalyticsProperties().WithFailure(item.Error));
        }

        context.Handler.Release.TrySetResult();
        await service.ShutdownAsync();
        var requests = context.Handler.Requests.Skip(1).ToArray();
        Check(requests.Length == cases.Length, "Every synthetic native failure reached the fake transport.");
        for (var index = 0; index < cases.Length; index++)
        {
            var properties = requests[index].Properties;
            Check(properties.GetProperty("native_error_code").GetInt32() == cases[index].Code, "Native error code is preserved.");
            Check(
                properties.GetProperty("error_hresult").GetInt32() == cases[index].Error.HResult,
                "HRESULT remains distinct from native code."
            );
            Check(
                properties.GetProperty("exception_type").GetString() == (cases[index].Component is null ? "win32" : "media_processing"),
                "Native exception family is allowlisted."
            );
            if (cases[index].Component is { } component)
            {
                Check(properties.GetProperty("error_component").GetString() == component, "The responsible media component is recorded.");
            }
            else
            {
                Check(!properties.TryGetProperty("error_component", out _), "A Win32 exception alone does not identify a component.");
            }

            Check(
                !requests[index].Raw.Contains("private", StringComparison.OrdinalIgnoreCase),
                "Native stderr or messages never enter analytics."
            );
        }
    }

    private static async Task WrappedErrorsAsync()
    {
        var cancelled = new OperationCanceledException(PrivateDetail);
        Exception eightWrappers = cancelled;
        for (var index = 0; index < 8; index++)
        {
            eightWrappers = new TargetInvocationException(PrivateDetail, eightWrappers);
        }

        var cases = new (Exception Error, bool Cancelled)[]
        {
            (cancelled, true),
            (new AggregateException(new TargetInvocationException(new TypeInitializationException(PrivateDetail, cancelled))), true),
            (eightWrappers, true),
            (new TargetInvocationException(PrivateDetail, eightWrappers), false),
            (new AggregateException(cancelled, new IOException(PrivateDetail)), false),
            (new InvalidOperationException(PrivateDetail, cancelled), false),
        };
        using var context = new TestContext(blockFirst: true);
        using var service = context.Create();
        Check(service.SetEnabled(true), "Enabled.");
        service.Track(AnalyticsEvent.AppOpened);
        await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var operationId = Guid.NewGuid();
        foreach (var item in cases)
        {
            var properties = new AnalyticsProperties { OperationId = operationId, FailureStage = AnalyticsGenerationStage.EncodingGif };
            service.Track(AnalyticsEvent.GifGenerationCompleted, properties.WithFailure(item.Error));
        }

        context.Handler.Release.TrySetResult();
        await service.ShutdownAsync();
        var requests = context.Handler.Requests.Skip(1).ToArray();
        Check(requests.Length == cases.Length, "Every wrapper example reached the fake transport.");
        for (var index = 0; index < cases.Length; index++)
        {
            var properties = requests[index].Properties;
            Check(
                properties.GetProperty("outcome").GetString() == (cases[index].Cancelled ? "cancelled" : "failed"),
                "Wrapper handling preserves the actual outcome."
            );
            Check(properties.GetProperty("operation_id").GetGuid() == operationId, "Adding failure metadata preserves the operation join.");
            Check(properties.GetProperty("failure_stage").GetString() == "encoding_gif", "Adding failure metadata preserves the stage.");
            if (cases[index].Cancelled)
            {
                Check(properties.GetProperty("error_kind").GetString() == "cancelled", "Cancellation is never reported as other.");
                Check(properties.GetProperty("exception_type").GetString() == "cancelled", "The unwrapped family is reported.");
                Check(properties.GetProperty("error_hresult").GetInt32() == cancelled.HResult, "The unwrapped HRESULT is reported.");
            }
        }
    }

    private static async Task GenerationPropertiesAsync()
    {
        using var context = new TestContext(blockFirst: true);
        using var service = context.Create();
        Check(service.SetEnabled(true), "Enabled.");
        service.Track(AnalyticsEvent.AppOpened);
        await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        service.Track(
            AnalyticsEvent.GifGenerationCompleted,
            new AnalyticsProperties
            {
                TargetFps = 0,
                SourceFps = 29.97,
                ExtractedFrameCount = 1222,
                FailureStage = AnalyticsGenerationStage.LoadingPreview,
                FailureStageDurationMs = 125.7,
                ExtractionDurationMs = 1000.3,
                EncodingDurationMs = 2000.7,
                PreviewDurationMs = 0,
            }
        );
        service.Track(
            AnalyticsEvent.GifGenerationCompleted,
            new AnalyticsProperties
            {
                TargetFps = 1000,
                SourceFps = 1000,
                ExtractedFrameCount = 10_000_000,
                FailureStageDurationMs = 86_400_000,
                ExtractionDurationMs = 86_400_000,
                EncodingDurationMs = 86_400_000,
                PreviewDurationMs = 86_400_000,
            }
        );
        foreach (var invalid in new[] { -1d, double.NaN, double.PositiveInfinity })
        {
            service.Track(
                AnalyticsEvent.GifGenerationCompleted,
                new AnalyticsProperties
                {
                    TargetFps = invalid,
                    SourceFps = invalid,
                    ExtractedFrameCount = 0,
                    FailureStage = (AnalyticsGenerationStage)999,
                    ExceptionType = (AnalyticsExceptionType)999,
                    ErrorComponent = (MediaProcessingComponent)999,
                    ErrorKind = (AnalyticsErrorKind)999,
                    FailureStageDurationMs = invalid,
                    ExtractionDurationMs = invalid,
                    EncodingDurationMs = invalid,
                    PreviewDurationMs = invalid,
                }
            );
        }

        service.Track(
            AnalyticsEvent.GifGenerationCompleted,
            new AnalyticsProperties
            {
                TargetFps = 1001,
                SourceFps = 1001,
                ExtractedFrameCount = 10_000_001,
                FailureStageDurationMs = 86_400_001,
                ExtractionDurationMs = 86_400_001,
                EncodingDurationMs = 86_400_001,
                PreviewDurationMs = 86_400_001,
            }
        );
        service.Track(AnalyticsEvent.GifGenerationCompleted, new AnalyticsProperties { SourceFps = 0 });
        context.Handler.Release.TrySetResult();
        await service.ShutdownAsync();
        var requests = context.Handler.Requests.Skip(1).ToArray();
        Check(requests.Length == 7, "All generation payload boundary cases were sent.");
        var actual = requests[0].Properties;
        Check(actual.GetProperty("requested_fps").GetDouble() == 0, "The existing zero FPS sentinel remains compatible.");
        Check(
            actual.GetProperty("requested_fps_mode").GetString() == "all_source_frames",
            "Zero FPS explicitly means use all source frames."
        );
        Check(actual.GetProperty("source_fps").GetDouble() == 29.97, "Actual source frame rate remains distinct from the request.");
        Check(actual.GetProperty("extracted_frame_count").GetInt32() == 1222, "Actual extracted frames are recorded.");
        Check(actual.GetProperty("failure_stage").GetString() == "loading_preview", "Failures identify a stable stage.");
        Check(actual.GetProperty("failure_stage_duration_ms").GetDouble() == 126, "Failure stage duration is rounded.");
        Check(actual.GetProperty("extraction_duration_ms").GetDouble() == 1000, "Extraction duration is rounded.");
        Check(actual.GetProperty("encoding_duration_ms").GetDouble() == 2001, "Encoding duration is rounded.");
        Check(actual.GetProperty("preview_duration_ms").GetDouble() == 0, "Zero elapsed time remains measurable.");
        var maximum = requests[1].Properties;
        Check(maximum.GetProperty("requested_fps_mode").GetString() == "target", "Positive requested FPS means an explicit target.");
        Check(maximum.GetProperty("source_fps").GetDouble() == 1000, "Maximum source FPS is retained.");
        Check(maximum.GetProperty("extracted_frame_count").GetInt32() == 10_000_000, "Maximum extracted frame count is retained.");
        foreach (
            var field in new[] { "failure_stage_duration_ms", "extraction_duration_ms", "encoding_duration_ms", "preview_duration_ms" }
        )
        {
            Check(maximum.GetProperty(field).GetDouble() == 86_400_000, "Maximum elapsed time is retained.");
        }

        Check(
            requests.Skip(2).All(request => request.Properties.EnumerateObject().Count() == 8),
            "Unknown enums and invalid numerics are omitted."
        );
    }

    private static async Task BlockedDeliveryAsync()
    {
        using var context = new TestContext(blockFirst: true, failFirst: true);
        using var service = context.Create();
        Check(service.SetEnabled(true), "Enabled.");
        service.Track(AnalyticsEvent.AppOpened);
        await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            var elapsed = await Task.Run(() =>
                {
                    var timer = Stopwatch.StartNew();
                    var properties = new AnalyticsProperties
                    {
                        FailureStage = AnalyticsGenerationStage.EncodingGif,
                        DurationMs = 1_550_301,
                        SourceFps = 30,
                        TargetFps = 0,
                    }.WithFailure(new MediaProcessingException(MediaProcessingComponent.Gifski, 9, PrivateDetail));
                    service.Track(AnalyticsEvent.GifGenerationCompleted, properties);
                    return timer.Elapsed;
                })
                .WaitAsync(TimeSpan.FromSeconds(2));
            Check(elapsed < TimeSpan.FromMilliseconds(500), "Detailed failure capture never waits for the blocked transport.");
        }
        finally
        {
            context.Handler.Release.TrySetResult();
        }

        await service.ShutdownAsync();
        Check(context.Handler.Requests.Count == 2, "A failed earlier request does not prevent generation failure delivery.");
        Check(
            context.Handler.Requests.Last().Properties.GetProperty("error_kind").GetString() == "invalid_media",
            "Detailed failure survives queue delivery."
        );
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

    private sealed class PrivateMediaException(string message) : Exception(message);
}
