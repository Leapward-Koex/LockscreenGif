using System.ComponentModel;
using System.Diagnostics;
using LockscreenGif.Services;

internal static class CapabilityTests
{
    public static async Task RunAsync()
    {
        await SharedSuccessfulDiscoveryAsync();
        await UnsupportedAndMultipleAdaptersAsync();
        await IncompleteChecksAndRetriesAsync();
        await BoundedDiscoveryAndDisposalAsync();
        await LateCapabilityFailureTests.RunAsync();
        VerifyProbeOutput();
    }

    private static async Task SharedSuccessfulDiscoveryAsync()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new FakeProbe((_, _, token) => release.Task.WaitAsync(token));
        using var service = Create(new FakeAdapters(0), probe);
        Check(service.Snapshot.Availability == HardwareDecodingAvailability.Checking, "initial state is checking");
        var first = service.EnsureCheckedAsync();
        Check(ReferenceEquals(first, service.EnsureCheckedAsync(true)), "concurrent discovery is shared");
        Check(!first.IsCompleted && service.Snapshot.AdapterIndex == null, "pending discovery has no usable adapter");
        service.Changed += (_, _) => throw new InvalidOperationException("Closed UI subscriber");
        release.SetResult(true);
        await first;
        Check(
            service.Snapshot == new HardwareDecodingCapability(HardwareDecodingAvailability.Available, 0),
            "successful decode retains adapter"
        );
        await service.EnsureCheckedAsync(true);
        Check(probe.Calls.Count == 1, "supported result is cached for the session");
    }

    private static async Task UnsupportedAndMultipleAdaptersAsync()
    {
        var noProbe = new FakeProbe((_, _, _) => Task.FromResult(true));
        using (var noAdapters = Create(new FakeAdapters(), noProbe))
        {
            await noAdapters.EnsureCheckedAsync();
            Check(
                noAdapters.Snapshot.Availability == HardwareDecodingAvailability.Unavailable && noProbe.Calls.Count == 0,
                "no hardware adapters"
            );
        }
        var unsupported = new FakeProbe((_, _, _) => Task.FromResult(false));
        using (var service = Create(new FakeAdapters(0, 2), unsupported))
        {
            await service.EnsureCheckedAsync();
            Check(
                service.Snapshot.Availability == HardwareDecodingAvailability.Unavailable,
                "completed negative probes establish unsupported"
            );
            Check(unsupported.Calls.Count == 8, "all four codecs tested on each adapter");
            Check(unsupported.Calls.Select(x => x.Sample).Distinct().Count() == 4, "codec probes are distinct");
            await service.EnsureCheckedAsync();
            Check(unsupported.Calls.Count == 8, "unsuccessful result is cached until Settings requests retry");
            await service.EnsureCheckedAsync(true);
            Check(unsupported.Calls.Count == 16, "Settings visit retries an unsupported result");
        }
        var multiple = new FakeProbe((adapter, sample, _) => Task.FromResult(adapter == 3 && sample == "vp9.webm"));
        using (var service = Create(new FakeAdapters(1, 3, 5), multiple))
        {
            await service.EnsureCheckedAsync();
            Check(service.Snapshot.AdapterIndex == 3 && multiple.Calls.Count == 7, "later adapter and codec succeed, then discovery stops");
        }
    }

    private static async Task IncompleteChecksAndRetriesAsync()
    {
        foreach (var failure in new Exception[] { new FileNotFoundException(), new Win32Exception(), new IOException() })
        {
            var probe = new FakeProbe((_, _, _) => Task.FromException<bool>(failure));
            using var service = Create(new FakeAdapters(0), probe);
            await service.EnsureCheckedAsync();
            Check(
                service.Snapshot.Availability == HardwareDecodingAvailability.CheckFailed,
                "missing asset or launch failure is not unsupported"
            );
            probe.Callback = (_, _, _) => Task.FromResult(true);
            await service.EnsureCheckedAsync();
            Check(service.Snapshot.Availability == HardwareDecodingAvailability.CheckFailed, "failure remains cached without retry");
            await service.EnsureCheckedAsync(true);
            Check(service.Snapshot.Availability == HardwareDecodingAvailability.Available, "next Settings visit recovers discovery");
        }
        var someFailure = new FakeProbe(
            (_, sample, _) => sample == "h264.mp4" ? Task.FromException<bool>(new IOException()) : Task.FromResult(true)
        );
        using (var service = Create(new FakeAdapters(0), someFailure))
        {
            await service.EnsureCheckedAsync();
            Check(
                service.Snapshot.Availability == HardwareDecodingAvailability.Available,
                "a later verified decode establishes support despite an earlier failure"
            );
        }
        using (var service = Create(new ThrowingAdapters(), new FakeProbe((_, _, _) => Task.FromResult(false))))
        {
            await service.EnsureCheckedAsync();
            Check(service.Snapshot.Availability == HardwareDecodingAvailability.CheckFailed, "adapter enumeration failure is check failed");
        }
        var missing = new FfmpegHardwareDecodingProbe(
            Path.Combine(Path.GetTempPath(), "missing-video-probes-" + Guid.NewGuid().ToString("N"))
        );
        using (
            var service = new HardwareDecodingCapabilityService(
                new FakeAdapters(0),
                missing,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2)
            )
        )
        {
            await service.EnsureCheckedAsync();
            Check(service.Snapshot.Availability == HardwareDecodingAvailability.CheckFailed, "real probe verifies bundled asset existence");
        }
    }

    private static async Task BoundedDiscoveryAndDisposalAsync()
    {
        var stalled = new FakeProbe(
            async (_, _, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return false;
            }
        );
        using (
            var service = new HardwareDecodingCapabilityService(
                new FakeAdapters(0),
                stalled,
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromSeconds(1)
            )
        )
        {
            await service.EnsureCheckedAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Check(
                service.Snapshot.Availability == HardwareDecodingAvailability.CheckFailed && stalled.Calls.Count == 1,
                "probe timeout remains inconclusive without overlapping cleanup"
            );
        }
        var ignoresToken = new FakeProbe((_, _, _) => new TaskCompletionSource<bool>().Task);
        using (
            var service = new HardwareDecodingCapabilityService(
                new FakeAdapters(0, 1),
                ignoresToken,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(40)
            )
        )
        {
            var timer = Stopwatch.StartNew();
            await service.EnsureCheckedAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Check(
                service.Snapshot.Availability == HardwareDecodingAvailability.CheckFailed && timer.Elapsed < TimeSpan.FromSeconds(2),
                "overall deadline bounds unresponsive probe"
            );
            Check(ignoresToken.Calls.Count == 1, "overall deadline prevents additional probes");
        }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellable = new FakeProbe(
            async (_, _, token) =>
            {
                entered.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                    cancelled.SetResult();
                    throw;
                }
                return false;
            }
        );
        using (var service = Create(new FakeAdapters(0), cancellable))
        {
            var running = service.EnsureCheckedAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            service.Dispose();
            await running.WaitAsync(TimeSpan.FromSeconds(3));
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await service.EnsureCheckedAsync(true);
            Check(cancellable.Calls.Count == 1, "shutdown cancels and never restarts a probe");
        }
    }

    private static void VerifyProbeOutput()
    {
        Check(FfmpegHardwareDecodingProbe.IsVerifiedOutput(0, 3, 0), "successful complete D3D11 probe accepted");
        foreach (var output in new[] { (0, 0, 3), (0, 0, 0), (1, 3, 0), (0, 2, 0), (0, 3, 1) })
        {
            Check(
                !FfmpegHardwareDecodingProbe.IsVerifiedOutput(output.Item1, output.Item2, output.Item3),
                "software, partial and failed probe rejected"
            );
        }
        Check(
            FfmpegHardwareDecodingProbe.ReadFrameFormat("[Parsed_showinfo_0] n:   2 pts: 1024 pts_time:0.0667 fmt:d3d11 cl:left")
                == "d3d11",
            "actual frame format recognized"
        );
        Check(
            FfmpegHardwareDecodingProbe.ReadFrameFormat("Hardware acceleration methods: d3d11va") == null,
            "compiled backend does not prove decoding support"
        );
        var arguments = FfmpegHardwareDecodingProbe.CreateArguments(3, "sample with spaces.mp4");
        Check(arguments[Array.IndexOf(arguments, "-hwaccel_device") + 1] == "3", "specific DXGI adapter selected");
        Check(
            arguments[Array.IndexOf(arguments, "-hwaccel_output_format") + 1] == "d3d11" && !arguments.Contains("hwdownload"),
            "probe requests GPU surfaces without readback"
        );
    }

    private static HardwareDecodingCapabilityService Create(IHardwareAdapterEnumerator adapters, IHardwareDecodingProbe probe) =>
        new(adapters, probe, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));

    private static void Check(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException("FAILED: " + message);
        }
        Console.WriteLine("PASS " + message);
    }

    private sealed class FakeAdapters(params int[] indices) : IHardwareAdapterEnumerator
    {
        public Task<IReadOnlyList<int>> EnumerateAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<int>>(indices);
    }

    private sealed class ThrowingAdapters : IHardwareAdapterEnumerator
    {
        public Task<IReadOnlyList<int>> EnumerateAsync(CancellationToken token) =>
            throw new InvalidOperationException("Enumeration failed");
    }

    private sealed class FakeProbe(Func<int, string, CancellationToken, Task<bool>> callback) : IHardwareDecodingProbe
    {
        public Func<int, string, CancellationToken, Task<bool>> Callback { get; set; } = callback;
        public List<(int Adapter, string Sample)> Calls { get; } = [];

        public Task<bool> IsSupportedAsync(int adapterIndex, string sampleName, CancellationToken token)
        {
            Calls.Add((adapterIndex, sampleName));
            return Callback(adapterIndex, sampleName, token);
        }
    }
}
