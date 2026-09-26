namespace LockscreenGif.Services.Analytics;

public enum AnalyticsEvent
{
    AppOpened,
    PageViewed,
    GifSelected,
    VideoLoadStarted,
    VideoLoadCompleted,
    GifGenerationStarted,
    GifGenerationCompleted,
    GifSaveCompleted,
    LockscreenApplyStarted,
    LockscreenApplyCompleted,
    LockscreenRemovalCompleted,
    DiagnosticTestRequested,
    DiagnosticStopRequested,
    DiagnosticReportExportCompleted,
    AppError,
    LogExportCompleted,
}

public enum AnalyticsOutcome
{
    Succeeded,
    Failed,
    Cancelled,
    Partial,
    NoChange,
}

public enum AnalyticsPage
{
    Lockscreen,
    Diagnostics,
    Settings,
}

public enum AnalyticsWorkflow
{
    Lockscreen,
    Diagnostics,
}

public enum AnalyticsErrorKind
{
    Cancelled,
    PermissionDenied,
    InvalidMedia,
    Io,
    Timeout,
    Other,
}
