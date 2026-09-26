namespace LockscreenGif.Models;

public sealed class LockscreenApplyResult
{
    public bool Success { get; set; }
    public bool Cancelled { get; set; }
    public bool ApiRequested { get; set; }
    public bool ApiCompleted { get; set; }
    public string? Error { get; set; }
    public List<LockscreenFileResult> Files { get; set; } = [];
}

public sealed class LockscreenFileResult
{
    public string Path { get; init; } = string.Empty;
    public bool Copied { get; set; }
    public bool Verified { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public string? Error { get; set; }
    public string? Sha256 { get; set; }
}

public sealed class LockscreenApplyEvent
{
    public string Stage { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? Path { get; init; }
    public string Severity { get; init; } = "Info";
}
