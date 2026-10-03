using System.Buffers.Binary;
using System.Text.Json;

namespace LockscreenGif.Privileged;

public sealed record HelperRequest(
    int Version,
    long Id,
    string Command,
    string? Path = null,
    bool Write = false,
    TraceScope? Scope = null,
    uint? FeatureId = null
);

public sealed record HelperReply(
    int Version,
    long Id,
    int ExitCode = 0,
    string? Error = null,
    TraceBatch? Batch = null,
    WindowsImageFeatureResult? WindowsImageFeature = null
);

public static class PipeProtocol
{
    public const int Version = 1;
    public const int MaximumMessageBytes = 4 * 1024 * 1024;

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumMessageBytes)
        {
            throw new InvalidDataException("Invalid helper message size.");
        }

        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Empty helper message.");
    }

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException("Helper message exceeds the size limit.");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }
}
