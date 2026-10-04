namespace LockscreenGif.Models;

/// <summary>Frame-index progress; a null fraction means that the video's duration is unknown.</summary>
public readonly record struct VideoIndexProgress(int FrameCount, double? Fraction);
