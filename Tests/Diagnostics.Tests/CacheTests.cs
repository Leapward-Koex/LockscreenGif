using LockscreenGif.Services.Diagnostics;

namespace Diagnostics.Tests;

internal static class CacheTests
{
    internal static async Task RunAsync(string directory)
    {
        var cache = Path.Combine(directory, "cache");
        Directory.CreateDirectory(cache);
        var path = Path.Combine(cache, "LockScreen.jpg");
        var source = await ReferenceAnimation.EnsureAsync(directory);
        File.Copy(source, path);
        var read = await CacheFileReader.ReadAsync(path, CancellationToken.None);
        Program.Check(
            read.Format == "GIF" && read.Stable && read.Sha256?.Length == 64 && read.HashReadAt is not null,
            "Cache content recognized by bytes, not JPEG filename"
        );
        using var collector = new CacheCollector(cache, (_, _) => { });
        var baseline = await collector.CaptureAsync("Baseline", true, CancellationToken.None);
        Program.Check(
            baseline.Complete && baseline.Files.Count == 1 && baseline.Files[0].Sha256 == read.Sha256,
            "Baseline inventory records source bytes"
        );
        await File.WriteAllBytesAsync(path, [255, 216, 255, 1, 2, 3]);
        var replaced = await collector.CaptureAsync("SessionLock", true, CancellationToken.None);
        Program.Check(
            replaced.Files[0].Format == "JPEG" && replaced.Files[0].Sha256 != read.Sha256,
            "Forced reconciliation detects replacement"
        );
        File.Delete(path);
        var missing = await collector.CaptureAsync("AfterUnlock", true, CancellationToken.None);
        Program.Check(missing.Complete && missing.Files.Count == 0, "Successful empty inventory is distinguishable");
        var unavailableWakeups = 0;
        using var absent = new CacheCollector(Path.Combine(directory, "absent"), (_, _) => { }, () => unavailableWakeups++);
        var unavailable = await absent.CaptureAsync("Baseline", true, CancellationToken.None);
        Program.Check(!unavailable.Complete && unavailable.Errors.Count > 0, "Unavailable directory is an incomplete inventory");
        Program.Check(unavailableWakeups == 0, "Watcher setup failure does not trigger a self-sustaining reconciliation loop");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await collector.CaptureAsync("Cancelled", true, cancelled.Token);
            throw new Exception("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("PASS Cache collection cancellation");
        }
    }
}
