using LockscreenGif.Models;

namespace LockscreenGif.Services.Analytics;

// Keep this a closed set of typed fields. Paths, media names, logs, and exception text must never enter analytics.
public sealed record AnalyticsProperties
{
    public AnalyticsOutcome? Outcome { get; init; }

    public AnalyticsPage? Page { get; init; }

    public Guid? OperationId { get; init; }

    public AnalyticsWorkflow? Workflow { get; init; }

    public LockscreenSourceKind? LockscreenSource { get; init; }

    public double? DurationMs { get; init; }

    // Requested conversion settings; these do not describe validated output media.
    public int? OutputWidth { get; init; }

    public double? TargetFps { get; init; }

    // Nominal source metadata, not the measured output frame rate (which may vary).
    public double? SourceFps { get; init; }

    public double? ClipDurationSeconds { get; init; }

    public int? SelectedFrameCount { get; init; }

    public int? ExtractedFrameCount { get; init; }

    public AnalyticsGenerationStage? FailureStage { get; init; }

    public double? FailureStageDurationMs { get; init; }

    public double? ExtractionDurationMs { get; init; }

    public double? EncodingDurationMs { get; init; }

    public double? PreviewDurationMs { get; init; }

    public bool? UsesReferenceGif { get; init; }

    // Actual source file size and GIF logical-screen dimensions, not requested conversion settings.
    public long? GifSizeBytes { get; init; }
    public int? GifWidth { get; init; }
    public int? GifHeight { get; init; }

    public int? TargetCount { get; init; }

    public int? CopiedCount { get; init; }

    public int? VerifiedCount { get; init; }

    public int? FailedCount { get; init; }

    public bool? ApiRequested { get; init; }

    public bool? ApiCompleted { get; init; }

    public LockscreenApplyFailureReason? ApplyFailureReason { get; init; }

    public AnalyticsErrorKind? ErrorKind { get; init; }

    public AnalyticsExceptionType? ExceptionType { get; init; }

    public int? ErrorHResult { get; init; }

    public MediaProcessingComponent? ErrorComponent { get; init; }

    public int? NativeErrorCode { get; init; }

    public AnalyticsProperties WithFailure(Exception exception) => AnalyticsErrorDetails.Apply(this, exception);

    public static AnalyticsErrorKind ClassifyError(Exception exception) => AnalyticsErrorDetails.Classify(exception);
}
