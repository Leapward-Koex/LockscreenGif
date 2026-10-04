using LockscreenGif.Services;

internal static class LateCapabilityFailureTests
{
    public static async Task RunAsync()
    {
        var unobserved = 0;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            if (args.Exception.Flatten().InnerExceptions.Any(exception => exception is LateDiscoveryException))
            {
                Interlocked.Increment(ref unobserved);
                args.SetObserved();
            }
        }
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await BlockedLaunchFailsAfterDeadlineAsync();
            await EnumerationFailsAfterDeadlineAsync();
            // Locals owning the failed operations are out of scope before finalization is forced.
            for (var iteration = 0; iteration < 4; iteration++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(25);
            }
            if (unobserved != 0)
            {
                throw new InvalidOperationException("Late discovery failures reached the application's unobserved-task handler.");
            }
            Console.WriteLine("PASS blocked native launch respects deadline and late probe/adapter faults are observed");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    private static async Task BlockedLaunchFailsAfterDeadlineAsync()
    {
        using var release = new ManualResetEventSlim();
        var probe = new BlockingLaunchProbe(release);
        using var service = new HardwareDecodingCapabilityService(
            new ImmediateAdapters(),
            probe,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(2)
        );
        var discovery = service.EnsureCheckedAsync();
        try
        {
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await discovery.WaitAsync(TimeSpan.FromSeconds(3));
            if (service.Snapshot.Availability != HardwareDecodingAvailability.CheckFailed || probe.Failed.Task.IsCompleted)
            {
                throw new InvalidOperationException("A blocked synchronous launch must time out before the launch function returns.");
            }
        }
        finally
        {
            release.Set();
        }
        await probe.Failed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(50);
    }

    private static async Task EnumerationFailsAfterDeadlineAsync()
    {
        var adapters = new DelayedAdapters();
        using var service = new HardwareDecodingCapabilityService(
            adapters,
            new UnusedProbe(),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(40)
        );
        await service.EnsureCheckedAsync().WaitAsync(TimeSpan.FromSeconds(3));
        if (service.Snapshot.Availability != HardwareDecodingAvailability.CheckFailed)
        {
            throw new InvalidOperationException("Adapter enumeration must respect the discovery deadline.");
        }
        adapters.Completion.SetException(new LateDiscoveryException());
        await Task.Delay(50);
    }

    private sealed class LateDiscoveryException : Exception;

    private sealed class BlockingLaunchProbe(ManualResetEventSlim release) : IHardwareDecodingProbe
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> IsSupportedAsync(int adapterIndex, string sampleName, CancellationToken token)
        {
            Entered.SetResult();
            release.Wait();
            Failed.SetResult();
            throw new LateDiscoveryException();
        }
    }

    private sealed class ImmediateAdapters : IHardwareAdapterEnumerator
    {
        public Task<IReadOnlyList<int>> EnumerateAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<int>>([0]);
    }

    private sealed class DelayedAdapters : IHardwareAdapterEnumerator
    {
        public TaskCompletionSource<IReadOnlyList<int>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<int>> EnumerateAsync(CancellationToken token) => Completion.Task;
    }

    private sealed class UnusedProbe : IHardwareDecodingProbe
    {
        public Task<bool> IsSupportedAsync(int adapterIndex, string sampleName, CancellationToken token) =>
            throw new InvalidOperationException("Enumeration never completed; the probe must not run.");
    }
}
