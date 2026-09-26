using LockscreenGif.Services.Diagnostics;

namespace Session.Tests;

internal static class MemorySessionTests
{
    public static async Task RunAsync(string root)
    {
        await using var context = await TestContext.CreateAsync(root, "memory-only");
        await context.Service.StartAsync(false, false);
        await context.Service.StopAsync();
        context.Service.RecordObservation("Animated correctly");
        var restarted = new DiagnosticsSessionService(context.Lockscreen, context.Windows, context.Factory);
        Program.Check(restarted.Current is null && !restarted.IsRunning, "new service starts without the previous test");
        Program.Check(!Directory.Exists(context.StoreDirectory), "no session checkpoints or retained sources were written");

        await context.Service.StartAsync(true, false);
        var reference = context.Service.Current!.SourcePath;
        Program.Check(context.Service.Current.Phase == "Waiting for lock", "reference animation still applies successfully");
        Program.Check(
            !File.Exists(reference) && !Directory.Exists(Path.GetDirectoryName(reference)),
            "reference working file is removed immediately after applying"
        );
        await context.Service.StopAsync();
        Program.Check(context.Service.Current!.Gif?.Sha256 is not null, "reference evidence remains in memory after cleanup");
    }
}
