using LockscreenGif.Contracts.Services;
using LockscreenGif.Services.Diagnostics;

namespace Session.Tests;

internal sealed class TestContext : IAsyncDisposable
{
    public string DirectoryPath { get; }
    public string StoreDirectory { get; }
    public FakeLockscreenService Lockscreen { get; }
    public WindowsSessionMonitor Windows { get; } = new();
    public DiagnosticsSessionService Service { get; }
    public FakePrivilegedSession Helper { get; } = new();
    public PrivilegedSessionFactory Factory { get; }

    private TestContext(string directory, IErrorReporter? errorReporter)
    {
        DirectoryPath = directory;
        StoreDirectory = Path.Combine(directory, "reports");
        var cache = Path.Combine(directory, "cache");
        Directory.CreateDirectory(cache);
        Lockscreen = new(cache);
        Factory = new(_ => Helper);
        Service = new(Lockscreen, Windows, Factory, errorReporter: errorReporter);
    }

    public static async Task<TestContext> CreateAsync(string root, string name, IErrorReporter? errorReporter = null)
    {
        var context = new TestContext(Path.Combine(root, name), errorReporter);
        context.Lockscreen.CurrentImage = new(await ReferenceAnimation.EnsureAsync(context.DirectoryPath));
        return context;
    }

    public async Task CompleteLockCycleAsync()
    {
        Windows.Emit("SessionLock");
        Windows.Emit("SessionUnlock");
        await Program.WaitUntilAsync(() => !Service.IsRunning, "Lock/unlock test did not finish.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Service.IsRunning)
        {
            await Service.StopAsync();
            await Program.WaitUntilAsync(() => !Service.IsRunning, "Session cleanup did not finish.");
        }
    }
}
