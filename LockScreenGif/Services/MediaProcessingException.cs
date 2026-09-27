namespace LockscreenGif.Services;

public enum MediaProcessingComponent
{
    Ffmpeg,
    Gifski,
}

// Keep the native result separate from diagnostic text, which may contain private paths.
public sealed class MediaProcessingException(MediaProcessingComponent component, int errorCode, string message) : Exception(message)
{
    public MediaProcessingComponent Component { get; } = component;

    public int ErrorCode { get; } = errorCode;
}
