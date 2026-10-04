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
    AnalyticsOptedOut,
    Exception,
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
    OutOfMemory,
    DiskFull,
    DependencyMissing,
    DependencyIncompatible,
    NativeFailure,
    InvalidState,
    InvalidArgument,
    CodecMissing,
    SecurityPolicyBlocked,
}

public enum AnalyticsExceptionType
{
    Cancelled,
    UnauthorizedAccess,
    Security,
    InvalidData,
    Format,
    Timeout,
    FileNotFound,
    DirectoryNotFound,
    Io,
    OutOfMemory,
    DllNotFound,
    EntryPointNotFound,
    BadImageFormat,
    Win32,
    Com,
    InvalidOperation,
    Argument,
    Aggregate,
    MediaProcessing,
    Other,
}

public enum AnalyticsGenerationStage
{
    Preparing,
    ExtractingFrames,
    EncodingGif,
    OpeningOutput,
    LoadingPreview,
    Completing,
}

public enum AnalyticsMediaLoadStage
{
    PickingFile,
    ReadingMetadata,
    IndexingFrames,
    OpeningPreview,
    OpeningFile,
    DecodingImage,
    Completing,
    PlayingPreview,
    ReadingFallbackMetadata,
}

public enum AnalyticsVideoIndexDecoder
{
    Cpu,
    D3D11,
}
