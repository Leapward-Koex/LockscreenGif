using System.Text;

namespace LockscreenGif.Services.Diagnostics;

/// <summary>A deterministic, simple test GIF; compatibility must still be observed on the test machine.</summary>
public static class ReferenceAnimation
{
    public static async Task<string> EnsureAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "diagnostics-reference-v1.gif");
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("GIF89a"));
            writer.Write((ushort)160);
            writer.Write((ushort)90);
            writer.Write(new byte[] { 0x80, 0, 0, 18, 28, 56, 255, 205, 48 });
            writer.Write(new byte[] { 0x21, 0xff, 11 });
            writer.Write(Encoding.ASCII.GetBytes("NETSCAPE2.0"));
            writer.Write(new byte[] { 3, 1, 0, 0, 0 });
            for (var frame = 0; frame < 4; frame++)
            {
                WriteFrame(writer, frame);
            }

            writer.Write((byte)0x3b);
        }
        await File.WriteAllBytesAsync(path, buffer.ToArray());
        return path;
    }

    private static void WriteFrame(BinaryWriter writer, int frame)
    {
        // A bright bar moves across a dark canvas every 300 ms. No flashing full-screen colors.
        writer.Write(new byte[] { 0x21, 0xf9, 4, 4, 30, 0, 0, 0, 0x2c });
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)160);
        writer.Write((ushort)90);
        writer.Write((byte)0);
        writer.Write((byte)2);
        // Clear the LZW dictionary before every pixel. This is larger but deterministic and tiny.
        var codes = new List<byte>(11000);
        var bits = 0;
        var bitCount = 0;
        void Code(int value)
        {
            bits |= value << bitCount;
            bitCount += 3;
            while (bitCount >= 8)
            {
                codes.Add((byte)bits);
                bits >>= 8;
                bitCount -= 8;
            }
        }
        for (var y = 0; y < 90; y++)
        {
            for (var x = 0; x < 160; x++)
            {
                Code(4);
                Code(y is >= 12 and < 78 && x >= frame * 36 + 8 && x < frame * 36 + 36 ? 1 : 0);
            }
        }

        Code(5);
        if (bitCount > 0)
        {
            codes.Add((byte)bits);
        }

        for (var offset = 0; offset < codes.Count; offset += 255)
        {
            var count = Math.Min(255, codes.Count - offset);
            writer.Write((byte)count);
            writer.Write(codes.GetRange(offset, count).ToArray());
        }
        writer.Write((byte)0);
    }
}
