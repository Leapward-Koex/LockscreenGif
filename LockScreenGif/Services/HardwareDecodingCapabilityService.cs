namespace LockscreenGif.Services;

public enum HardwareDecodingAvailability
{
    Checking,
    Available,
    Unavailable,
    CheckFailed,
}

public sealed record HardwareDecodingCapability(HardwareDecodingAvailability Availability, int? AdapterIndex = null);

/// <summary>One bounded, shared hardware discovery operation; notifications are raised on a worker thread.</summary>
public sealed class HardwareDecodingCapabilityService : IDisposable
{
    private static readonly string[] SampleNames = ["h264.mp4", "hevc.mp4", "vp9.webm", "av1.ivf"];
    private readonly object _gate = new();
    private readonly IHardwareAdapterEnumerator _adapters;
    private readonly IHardwareDecodingProbe _probe;
    private readonly TimeSpan _probeTimeout;
    private readonly TimeSpan _discoveryTimeout;
    private readonly CancellationTokenSource _lifetime = new();
    private HardwareDecodingCapability _snapshot = new(HardwareDecodingAvailability.Checking);
    private Task? _discovery;
    private bool _disposed;

    public HardwareDecodingCapabilityService()
        : this(new DxgiHardwareAdapterEnumerator(), new FfmpegHardwareDecodingProbe(), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15))
    { }

    internal HardwareDecodingCapabilityService(
        IHardwareAdapterEnumerator adapters,
        IHardwareDecodingProbe probe,
        TimeSpan probeTimeout,
        TimeSpan discoveryTimeout
    )
    {
        _adapters = adapters;
        _probe = probe;
        _probeTimeout = probeTimeout;
        _discoveryTimeout = discoveryTimeout;
    }

    public event EventHandler? Changed;

    public HardwareDecodingCapability Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public Task EnsureCheckedAsync(bool retryUnsuccessful = false)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }
            if (_discovery is { IsCompleted: false })
            {
                return _discovery;
            }
            if (_discovery != null && (!retryUnsuccessful || _snapshot.Availability == HardwareDecodingAvailability.Available))
            {
                return _discovery;
            }

            _snapshot = new(HardwareDecodingAvailability.Checking);
            _discovery = Task.Run(DiscoverAsync);
            return _discovery;
        }
    }

    private async Task DiscoverAsync()
    {
        OnChanged();
        var result = new HardwareDecodingCapability(HardwareDecodingAvailability.CheckFailed);
        using var discovery = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        discovery.CancelAfter(_discoveryTimeout);
        try
        {
            var enumeration = _adapters.EnumerateAsync(discovery.Token);
            ObserveFailure(enumeration);
            var adapters = await enumeration.WaitAsync(discovery.Token).ConfigureAwait(false);
            var failed = false;
            foreach (var adapter in adapters)
            {
                foreach (var sample in SampleNames)
                {
                    discovery.Token.ThrowIfCancellationRequested();
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(discovery.Token);
                    attempt.CancelAfter(_probeTimeout);
                    var attemptToken = attempt.Token;
                    try
                    {
                        // Bound process launch too: native startup can block before a probe's first await.
                        var probe = Task.Run(() => _probe.IsSupportedAsync(adapter, sample, attemptToken), attemptToken);
                        ObserveFailure(probe);
                        if (await probe.WaitAsync(attemptToken).ConfigureAwait(false))
                        {
                            discovery.Token.ThrowIfCancellationRequested();
                            result = new(HardwareDecodingAvailability.Available, adapter);
                            Publish(result);
                            return;
                        }
                    }
                    catch (OperationCanceledException) when (!discovery.IsCancellationRequested)
                    {
                        // Do not launch another process while a timed-out driver's cleanup might still be pending.
                        Publish(result);
                        return;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // An incomplete check cannot establish that the machine lacks hardware support.
                        failed = true;
                    }
                }
            }
            discovery.Token.ThrowIfCancellationRequested();
            result = new(failed ? HardwareDecodingAvailability.CheckFailed : HardwareDecodingAvailability.Unavailable);
        }
        catch (Exception)
        {
            // Detection is optional and must never prevent application startup or Settings navigation.
        }
        Publish(result);
    }

    private void Publish(HardwareDecodingCapability snapshot)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _snapshot = snapshot;
        }
        OnChanged();
    }

    private void OnChanged()
    {
        var handlers = Changed;
        if (handlers == null)
        {
            return;
        }
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception)
            {
                // A closed Settings page must not fault shared discovery for future video loads.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        ObserveFailure(_lifetime.CancelAsync());
        // Do not wait for FFmpeg on the UI thread. Its cancellation registration stops and drains it.
    }

    private static void ObserveFailure(Task operation)
    {
        // A deadline can finish discovery before native launch/cleanup finishes. Observe any later fault;
        // the application's unobserved-task handler must never turn optional discovery into a fatal error.
        _ = operation.ContinueWith(
            static failed =>
            {
                _ = failed.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }
}

internal interface IHardwareAdapterEnumerator
{
    Task<IReadOnlyList<int>> EnumerateAsync(CancellationToken token);
}

internal interface IHardwareDecodingProbe
{
    Task<bool> IsSupportedAsync(int adapterIndex, string sampleName, CancellationToken token);
}
