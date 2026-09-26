namespace LockscreenGif.Services;

public sealed class DeleteFilesResult
{
    public int SuccessfulDeletions { get; set; }
    public int FailedDeletions { get; set; }
}
