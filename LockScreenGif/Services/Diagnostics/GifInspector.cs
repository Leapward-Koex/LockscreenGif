using System.Security.Cryptography;
using System.Text;
using LockscreenGif.Models.Diagnostics;

namespace LockscreenGif.Services.Diagnostics;

/// <summary>Reads GIF structure without allocating a decoded image or loading the file into memory.</summary>
public static class GifInspector
{
    public static Task<GifInspection> InspectAsync(string path, CancellationToken token = default) =>
        Task.Run(
            async () =>
            {
                var result = new GifInspection();
                try
                {
                    await using var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        65536,
                        FileOptions.SequentialScan | FileOptions.Asynchronous
                    );
                    result.SizeBytes = stream.Length;
                    result.Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
                    stream.Position = 0;
                    using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
                    ReadStructure(reader, result, token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result.Error = $"{ex.GetType().Name}: {ex.Message}";
                }
                return result;
            },
            token
        );

    private static void ReadStructure(BinaryReader reader, GifInspection result, CancellationToken token)
    {
        var signature = Encoding.ASCII.GetString(ReadExact(reader, 6));
        result.Format = signature is "GIF87a" or "GIF89a" ? "GIF" : "Unknown";
        if (result.Format != "GIF")
        {
            throw new InvalidDataException("The source is not a GIF87a or GIF89a file.");
        }

        result.Width = reader.ReadUInt16();
        result.Height = reader.ReadUInt16();
        if (result.Width == 0 || result.Height == 0)
        {
            throw new InvalidDataException("The GIF canvas has zero size.");
        }

        var packed = reader.ReadByte();
        Skip(reader, 2);
        SkipPalette(reader, packed);
        ushort frameDelay = 0;
        var missingOrZeroDelays = 0;
        var hasTrailer = false;
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            token.ThrowIfCancellationRequested();
            var marker = reader.ReadByte();
            if (marker == 0x3b)
            {
                hasTrailer = true;
                break;
            }
            if (marker == 0x21)
            {
                var kind = reader.ReadByte();
                if (kind == 0xf9)
                {
                    if (reader.ReadByte() != 4)
                    {
                        throw new InvalidDataException("Invalid GIF graphics control block.");
                    }

                    reader.ReadByte();
                    frameDelay = reader.ReadUInt16();
                    reader.ReadByte();
                    if (reader.ReadByte() != 0)
                    {
                        throw new InvalidDataException("Unterminated GIF graphics control block.");
                    }
                }
                else if (kind == 0xff)
                {
                    var app = Encoding.ASCII.GetString(ReadExact(reader, reader.ReadByte()));
                    ReadSubBlocks(
                        reader,
                        token,
                        app is "NETSCAPE2.0" or "ANIMEXTS1.0"
                            ? block =>
                            {
                                if (block.Length >= 3 && block[0] == 1)
                                {
                                    result.LoopCount = block[1] | block[2] << 8;
                                }
                            }
                            : null
                    );
                }
                else
                {
                    ReadSubBlocks(reader, token);
                }

                continue;
            }
            if (marker != 0x2c)
            {
                throw new InvalidDataException($"Unexpected GIF block 0x{marker:X2}.");
            }

            var left = reader.ReadUInt16();
            var top = reader.ReadUInt16();
            var width = reader.ReadUInt16();
            var height = reader.ReadUInt16();
            if (width == 0 || height == 0 || left + width > result.Width || top + height > result.Height)
            {
                throw new InvalidDataException("A GIF frame falls outside its canvas.");
            }

            SkipPalette(reader, reader.ReadByte());
            var codeSize = reader.ReadByte();
            if (codeSize is < 2 or > 8)
            {
                throw new InvalidDataException("Invalid GIF LZW code size.");
            }

            if (ReadSubBlocks(reader, token) == 0)
            {
                throw new InvalidDataException("A GIF frame has no image data.");
            }

            result.FrameCount++;
            result.DurationSeconds += frameDelay / 100d;
            if (frameDelay == 0)
            {
                missingOrZeroDelays++;
            }

            frameDelay = 0;
        }
        if (!hasTrailer)
        {
            throw new InvalidDataException("The GIF trailer is missing; the file may be truncated.");
        }

        if (result.FrameCount == 0)
        {
            throw new InvalidDataException("The GIF contains no image frames.");
        }

        if (result.FrameCount == 1)
        {
            result.Warnings.Add("This file contains only one frame.");
        }

        if (missingOrZeroDelays > 0)
        {
            result.Warnings.Add($"{missingOrZeroDelays} frame(s) have zero or unspecified delay; playback timing depends on the decoder.");
        }

        if (result.LoopCount is null)
        {
            result.Warnings.Add("No loop extension is present; a decoder may play this GIF only once.");
        }
        else if (result.LoopCount > 0)
        {
            result.Warnings.Add($"The GIF requests a finite loop count ({result.LoopCount}).");
        }

        if (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            result.Warnings.Add("The file contains bytes after the GIF trailer.");
        }

        result.Warnings.Add(
            "Structure and timing were inspected without decompressing pixels; this does not prove Windows can render every frame."
        );
    }

    private static int ReadSubBlocks(BinaryReader reader, CancellationToken token, Action<byte[]>? inspect = null)
    {
        var count = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var size = reader.ReadByte();
            if (size == 0)
            {
                return count;
            }

            if (inspect is null)
            {
                Skip(reader, size);
            }
            else
            {
                inspect(ReadExact(reader, size));
            }

            count = Math.Min(int.MaxValue - 255, count) + size;
        }
    }

    private static void SkipPalette(BinaryReader reader, byte packed)
    {
        if ((packed & 0x80) != 0)
        {
            Skip(reader, 3 * (1 << ((packed & 7) + 1)));
        }
    }

    private static byte[] ReadExact(BinaryReader reader, int count)
    {
        var bytes = reader.ReadBytes(count);
        if (bytes.Length != count)
        {
            throw new EndOfStreamException("The GIF is truncated.");
        }

        return bytes;
    }

    private static void Skip(BinaryReader reader, int count)
    {
        if (reader.BaseStream.Length - reader.BaseStream.Position < count)
        {
            throw new EndOfStreamException("The GIF is truncated.");
        }

        reader.BaseStream.Seek(count, SeekOrigin.Current);
    }
}
