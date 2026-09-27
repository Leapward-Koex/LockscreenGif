namespace LockscreenGif.Services.Analytics;

// Keep this a closed set of typed fields. Paths, media names, logs, and exception text must never enter analytics.
public sealed record AnalyticsProperties
{
    public AnalyticsOutcome? Outcome { get; init; }

    public AnalyticsPage? Page { get; init; }

    public Guid? OperationId { get; init; }

    public AnalyticsWorkflow? Workflow { get; init; }

    public double? DurationMs { get; init; }

    // Requested conversion settings; these do not describe validated output media.
    public int? OutputWidth { get; init; }

    public double? TargetFps { get; init; }

    public double? ClipDurationSeconds { get; init; }

    public int? SelectedFrameCount { get; init; }

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

    public AnalyticsErrorKind? ErrorKind { get; init; }

    public static AnalyticsErrorKind ClassifyError(Exception exception) =>
        exception switch
        {
            OperationCanceledException => AnalyticsErrorKind.Cancelled,
            UnauthorizedAccessException or System.Security.SecurityException => AnalyticsErrorKind.PermissionDenied,
            InvalidDataException or FormatException => AnalyticsErrorKind.InvalidMedia,
            TimeoutException => AnalyticsErrorKind.Timeout,
            IOException => AnalyticsErrorKind.Io,
            _ => AnalyticsErrorKind.Other,
        };
}
