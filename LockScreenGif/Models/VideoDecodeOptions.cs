namespace LockscreenGif.Models;

/// <summary>A load-time snapshot; later preference or discovery changes affect the next load.</summary>
public sealed record VideoDecodeOptions(bool UseHardwareDecoding, int? AdapterIndex)
{
    public bool CanUseHardware => UseHardwareDecoding && AdapterIndex is >= 0;
}

public enum VideoIndexDecoder
{
    Cpu,
    D3D11,
}

public sealed record VideoIndexingResult(VideoFrameIndex Index, VideoIndexDecoder Decoder, bool HardwareFallbackUsed, TimeSpan Duration);
