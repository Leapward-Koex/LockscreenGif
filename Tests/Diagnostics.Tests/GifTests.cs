using LockscreenGif.Services.Diagnostics;

namespace Diagnostics.Tests;

internal static class GifTests
{
    internal static async Task RunAsync(string directory)
    {
        var source = await ReferenceAnimation.EnsureAsync(directory);
        var gif = await GifInspector.InspectAsync(source);
        Program.Check(
            gif.Error is null
                && gif.IsAnimated
                && gif.FrameCount == 4
                && gif.LoopCount == 0
                && gif.Width == 160
                && gif.Height == 90
                && Math.Abs(gif.DurationSeconds - 1.2) < 0.001,
            "Reference metadata includes dimensions, animation, timing, and infinite loop"
        );
        Program.Check(gif.Sha256.Length == 64 && gif.SizeBytes == new FileInfo(source).Length, "Source hash and length recorded");
        var original = await File.ReadAllBytesAsync(source);
        await ReferenceAnimation.EnsureAsync(directory);
        var regenerated = await File.ReadAllBytesAsync(source);
        Program.Check(original.SequenceEqual(regenerated), "Reference animation is deterministic");
        var broken = Path.Combine(directory, "broken.gif");
        await File.WriteAllBytesAsync(broken, original[..^8]);
        Program.Check((await GifInspector.InspectAsync(broken)).Error is not null, "Truncated GIF rejected");
        var nonGif = Path.Combine(directory, "not.gif");
        await File.WriteAllTextAsync(nonGif, "not a GIF");
        Program.Check((await GifInspector.InspectAsync(nonGif)).Error is not null, "Disguised non-GIF rejected");
        var emptyCanvas = original.ToArray();
        emptyCanvas[6] = emptyCanvas[7] = 0;
        await File.WriteAllBytesAsync(broken, emptyCanvas);
        Program.Check((await GifInspector.InspectAsync(broken)).Error is not null, "Zero-width canvas rejected");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await GifInspector.InspectAsync(source, cancelled.Token);
            throw new Exception("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("PASS GIF inspection cancellation");
        }
    }
}
