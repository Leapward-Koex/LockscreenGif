using System.Buffers.Binary;
using LockscreenGif.Privileged;

namespace ProcessTracing.Tests;

internal static class ProtocolTests
{
    public static async Task RunAsync()
    {
        using var stream = new MemoryStream();
        await PipeProtocol.WriteAsync(stream, new HelperRequest(1, 9, "Grant", @"C:\cache\LockScreen.jpg", true), default);
        stream.Position = 0;
        var request = await PipeProtocol.ReadAsync<HelperRequest>(stream, default);
        Program.Check(request.Id == 9 && request.Write, "Versioned protocol round trip");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, PipeProtocol.MaximumMessageBytes + 1);
        using var invalid = new MemoryStream(header);
        try
        {
            await PipeProtocol.ReadAsync<HelperRequest>(invalid, default);
            throw new Exception("Oversized request accepted");
        }
        catch (InvalidDataException)
        {
            Program.Check(true, "Oversized requests rejected before allocation");
        }
    }
}
