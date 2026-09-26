using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using LockscreenGif.Privileged;
using LockscreenGif.Services.Lockscreen;

namespace ProcessTracing.Tests;

// Opt-in integration check staged as LockscreenGif.exe beside the packaged helper,
// so the production peer PID, executable location and initiating SID checks all run.
internal static class NativeTransportTests
{
    public static async Task RunAsync(string report)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!.Value;
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft",
            "Windows",
            "SystemData",
            sid,
            "ReadOnly"
        );
        var fixture = Path.Combine(Path.GetTempPath(), "LockscreenGif-Transport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var source = Path.Combine(fixture, "LockScreen.jpg");
        await File.WriteAllBytesAsync(source, new byte[8192]);
        var launches = 0;
        var helperPid = 0;
        var operations = new List<TraceOperation>();
        try
        {
            await using (
                var session = new CachePermissionSession(
                    root,
                    sid,
                    start =>
                    {
                        launches++;
                        var process = Process.Start(start)!;
                        helperPid = process.Id;
                        return process;
                    }
                )
            )
            {
                await session.StartTraceAsync(new(root, source), default);
                // Rejection exercises real helper routing without changing any permissions.
                if (await session.GrantAsync(source, true, default) == 0)
                {
                    throw new InvalidOperationException("Helper accepted a permission repair outside its cache.");
                }

                _ = File.ReadAllBytes(source);
                await Task.Delay(1500);
                await session.StopTraceAsync(default);
                TraceBatch batch;
                do
                {
                    batch = await session.ReadTraceAsync(default);
                    operations.AddRange(batch.Operations);
                } while (batch.HasMore);
                if (
                    launches != 1
                    || !operations.Any(o =>
                        o.ProcessId == Environment.ProcessId && o.IsApp && o.Operation == "Read" && o.Succeeded && o.CompletedBytes == 8192
                    )
                )
                {
                    throw new InvalidOperationException("Authenticated helper did not return the fixture read.");
                }

                await File.WriteAllTextAsync(
                    report,
                    JsonSerializer.Serialize(
                        new
                        {
                            Launches = launches,
                            HelperPid = helperPid,
                            batch.Evidence,
                            Operations = operations,
                        },
                        new JsonSerializerOptions { WriteIndented = true }
                    )
                );
            }
            try
            {
                using var helper = Process.GetProcessById(helperPid);
                if (!helper.HasExited)
                {
                    throw new InvalidOperationException("Helper remained after pipe disposal.");
                }
            }
            catch (ArgumentException) { }
        }
        finally
        {
            File.Delete(source);
            Directory.Delete(fixture);
        }
    }
}
