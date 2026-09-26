using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using LockscreenGif.Privileged.Helper.Tracing;

namespace ProcessTracing.Tests;

internal static class NativeCollectorTests
{
    public static void Fixture(string root)
    {
        var path = Path.Combine(root, "LockScreen.jpg");
        File.WriteAllBytes(path, new byte[8192]);
        _ = File.ReadAllBytes(path);
        var renamed = Path.Combine(root, "LockScreen_renamed.jpg");
        File.Move(path, renamed);
        _ = File.ReadAllBytes(renamed);
        File.Delete(renamed);
        try
        {
            using var missing = File.OpenRead(path);
            throw new InvalidOperationException("Deleted fixture unexpectedly opened.");
        }
        catch (FileNotFoundException) { }
    }

    public static async Task RunAsync(string report)
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new InvalidOperationException("Native fixture requires elevation.");
        }

        var root = Path.Combine(Path.GetTempPath(), "LockscreenGif-TraceFixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var collector = new EtwFileCollector();
        try
        {
            await collector.StartAsync(new(root, Path.Combine(root, "LockScreen.jpg")), 0);
            var conflict = new EtwFileCollector();
            try
            {
                await conflict.StartAsync(new(root, Path.Combine(root, "LockScreen.jpg")), 0);
                throw new InvalidOperationException("A second collector acquired the active trace session.");
            }
            catch (IOException) { }
            finally
            {
                await conflict.StopAsync();
            }
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("--fixture");
            info.ArgumentList.Add(root);
            using var child = Process.Start(info)!;
            var pid = child.Id;
            await child.WaitForExitAsync();
            await Task.Delay(2000);
            await collector.StopAsync();
            var batches = new List<LockscreenGif.Privileged.TraceOperation>();
            LockscreenGif.Privileged.TraceBatch batch;
            do
            {
                batch = collector.Drain();
                batches.AddRange(batch.Operations);
            } while (batch.HasMore);
            var evidence = JsonSerializer.Serialize(
                new
                {
                    ChildPid = pid,
                    batch.Evidence,
                    Operations = batches,
                },
                new JsonSerializerOptions { WriteIndented = true }
            );
            await File.WriteAllTextAsync(report, evidence);
            var shutdown = batch.Evidence.Shutdown;
            if (
                shutdown?.StopRequestedAt is null
                || shutdown.NativeStopReturnedAt is null
                || shutdown.Current.CallbacksFinished == 0
                || shutdown.ConsumerReturnedAt is null
                || shutdown.CleanupCompletedAt is null
            )
            {
                throw new InvalidOperationException("Native trace lacks shutdown or consumer progress evidence; see " + report);
            }

            foreach (var operation in new[] { "Open", "Read", "Write", "Rename", "Delete" })
            {
                if (!batches.Any(o => o.ProcessId == pid && o.Operation == operation && o.Succeeded))
                {
                    throw new InvalidOperationException(
                        "Native trace did not capture successful " + operation + "; see " + report + ". Evidence: " + evidence
                    );
                }
            }

            if (!batches.Any(o => o.ProcessId == pid && o.Operation == "Open" && o.Status is uint status && (status & 0x80000000) != 0))
            {
                throw new InvalidOperationException("Native trace did not preserve the failed file open.");
            }

            var restarted = new EtwFileCollector();
            await restarted.StartAsync(new(root, Path.Combine(root, "LockScreen.jpg")), 0);
            await restarted.StopAsync();
            if (Directory.EnumerateFiles(root, "*.etl", SearchOption.AllDirectories).Any())
            {
                throw new InvalidOperationException("Unexpected trace file.");
            }
        }
        finally
        {
            await collector.StopAsync();
            foreach (var file in Directory.EnumerateFiles(root))
            {
                File.Delete(file);
            }

            Directory.Delete(root);
        }
    }
}
