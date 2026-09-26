using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using LockscreenGif.Services.Analytics;

internal static class ReliabilityTests
{
    public static async Task RunAsync()
    {
        await Run("session IDs rotate at 30-minute inactivity and 24-hour duration boundaries", SessionBoundariesAsync);
        await Run("queued events retain capture timestamps, sessions, operation IDs, and unique UUIDs", SnapshotAsync);
        await Run("new workflow events and bounded conversion settings use the allowlist", WorkflowPropertiesAsync);
        await Run("DNS failures and 403, 429, and 500 responses remain best effort without retries", RejectedRequestsAsync);
        await Run("synchronous HTTP startup cannot delay capture, opt-out, stop, or dispose", SynchronousTransportAsync);
        await Run("a transport ignoring cancellation cannot delay immediate stop or bounded draining", UncooperativeTransportAsync);
        await Run("blocking cancellation callbacks never run on application callers", BlockingCallbacksAsync);
        await Run("concurrent capture stays bounded and stop remains responsive", ConcurrentCaptureAsync);
    }

    private static async Task Run(string message, Func<Task> test)
    {
        await test();
        Console.WriteLine("PASS " + message);
    }

    private static async Task SessionBoundariesAsync()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
        using var context = new TestContext(blockFirst: true);
        using var service = context.Create(timeProvider: clock);
        Check(service.SetEnabled(true), "Enabled.");
        service.Track(AnalyticsEvent.AppOpened);
        await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Queue while the first request is blocked so event times and session boundaries cannot depend on transport timing.
        clock.Advance(TimeSpan.FromMinutes(30) - TimeSpan.FromTicks(1));
        service.Track(AnalyticsEvent.PageViewed);
        clock.Advance(TimeSpan.FromMinutes(30));
        service.Track(AnalyticsEvent.GifSelected);
        var sessionStart = clock.GetUtcNow();
        for (var index = 1; index <= 49; index++)
        {
            clock.Set(sessionStart + TimeSpan.FromMinutes(index * 29));
            service.Track(AnalyticsEvent.GifSelected);
        }

        clock.Set(sessionStart + TimeSpan.FromHours(24) - TimeSpan.FromTicks(1));
        service.Track(AnalyticsEvent.PageViewed);
        clock.Advance(TimeSpan.FromTicks(1));
        service.Track(AnalyticsEvent.GifSelected);
        context.Handler.Release.TrySetResult();
        await service.ShutdownAsync();

        var sessions = context.Handler.Requests.Select(request => request.Properties.GetProperty("$session_id").GetString()).ToArray();
        Check(sessions.Length == 54, "All gated boundary samples were delivered.");
        Check(sessions[0] == sessions[1], "An event just before 30 minutes continues its session.");
        Check(sessions[1] != sessions[2], "Exactly 30 minutes of inactivity starts a new session.");
        Check(sessions.Skip(2).Take(51).All(session => session == sessions[2]), "Activity retains the session until just before 24 hours.");
        Check(sessions[^1] != sessions[^2], "Exactly 24 hours starts a new session despite continuing activity.");
        Check(sessions.All(session => session![14] == '7'), "Every session ID is UUIDv7.");
    }

    private static async Task SnapshotAsync()
    {
        var initial = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(initial);
        using var context = new TestContext(blockFirst: true);
        using var service = context.Create(timeProvider: clock);
        Check(service.SetEnabled(true), "Enabled.");
        service.Track(AnalyticsEvent.AppOpened);
        await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var operationId = Guid.NewGuid();
        clock.Advance(TimeSpan.FromMinutes(1));
        service.Track(AnalyticsEvent.GifGenerationStarted, new AnalyticsProperties { OperationId = operationId });
        clock.Advance(TimeSpan.FromMinutes(31));
        service.Track(AnalyticsEvent.GifGenerationCompleted, new AnalyticsProperties { OperationId = operationId });
        clock.Advance(TimeSpan.FromDays(1));
        context.Handler.Release.TrySetResult();
        await service.ShutdownAsync();

        var requests = context.Handler.Requests.ToArray();
        Check(requests.Length == 3, "The gated snapshots were delivered.");
        Check(requests[0].Root.GetProperty("timestamp").GetDateTimeOffset() == initial, "Initial capture timestamp remains fixed.");
        Check(
            requests[1].Root.GetProperty("timestamp").GetDateTimeOffset() == initial.AddMinutes(1),
            "Queue delay never changes the event time."
        );
        Check(
            requests[2].Root.GetProperty("timestamp").GetDateTimeOffset() == initial.AddMinutes(32),
            "Completion keeps its own capture time."
        );
        Check(
            requests[1].Properties.GetProperty("$session_id").GetString() != requests[2].Properties.GetProperty("$session_id").GetString(),
            "Session boundaries reflect capture activity even while requests are delayed."
        );
        Check(
            requests.Skip(1).All(request => request.Properties.GetProperty("operation_id").GetGuid() == operationId),
            "Operation joins survive a session boundary."
        );
        Check(
            requests.Select(request => request.Root.GetProperty("uuid").GetGuid()).Distinct().Count() == 3,
            "Each event has a distinct deduplication UUID."
        );
    }

    private static async Task WorkflowPropertiesAsync()
    {
        using var context = new TestContext(blockFirst: true);
        using var service = context.Create();
        Check(service.SetEnabled(true), "Enabled.");
        service.Track(AnalyticsEvent.AppOpened);
        await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var names = new Dictionary<AnalyticsEvent, string>
        {
            [AnalyticsEvent.VideoLoadStarted] = "video_load_started",
            [AnalyticsEvent.GifGenerationStarted] = "gif_generation_started",
            [AnalyticsEvent.LockscreenApplyStarted] = "lockscreen_apply_started",
            [AnalyticsEvent.DiagnosticTestRequested] = "diagnostic_test_requested",
            [AnalyticsEvent.DiagnosticStopRequested] = "diagnostic_stop_requested",
            [AnalyticsEvent.DiagnosticReportExportCompleted] = "diagnostic_report_export_completed",
            [AnalyticsEvent.LogExportCompleted] = "log_export_completed",
        };
        foreach (var eventName in names.Keys)
        {
            service.Track(eventName);
        }

        service.Track(
            AnalyticsEvent.GifGenerationStarted,
            new AnalyticsProperties
            {
                Workflow = AnalyticsWorkflow.Lockscreen,
                OutputWidth = 32768,
                TargetFps = 1000,
                ClipDurationSeconds = 86400,
                SelectedFrameCount = 10_000_000,
            }
        );
        service.Track(
            AnalyticsEvent.GifGenerationStarted,
            new AnalyticsProperties
            {
                OutputWidth = 0,
                TargetFps = -1,
                ClipDurationSeconds = -1,
                SelectedFrameCount = 0,
            }
        );
        service.Track(
            AnalyticsEvent.GifGenerationStarted,
            new AnalyticsProperties
            {
                OutputWidth = 32769,
                TargetFps = 1001,
                ClipDurationSeconds = 86401,
                SelectedFrameCount = 10_000_001,
            }
        );
        context.Handler.Release.TrySetResult();
        await service.ShutdownAsync();

        var requests = context.Handler.Requests.ToArray();
        Check(
            requests.Skip(1).Take(names.Count).Select(request => request.Root.GetProperty("event").GetString()).SequenceEqual(names.Values),
            "New events use their approved wire names."
        );
        var valid = requests[^3].Properties;
        Check(valid.GetProperty("workflow").GetString() == "lockscreen", "Lockscreen workflow serializes explicitly.");
        Check(
            valid.GetProperty("requested_width").GetInt32() == 32768 && valid.GetProperty("requested_fps").GetDouble() == 1000,
            "Maximum valid requested width and FPS are retained."
        );
        Check(
            valid.GetProperty("clip_duration_seconds").GetDouble() == 86400
                && valid.GetProperty("selected_frame_count").GetInt32() == 10_000_000,
            "Maximum valid clip and frame values are retained."
        );
        Check(
            requests.TakeLast(2).All(request => request.Properties.EnumerateObject().Count() == 8),
            "Out-of-range values on both sides are dropped."
        );
    }

    private static async Task RejectedRequestsAsync()
    {
        foreach (var response in new[] { 0, 403, 429, 500 })
        {
            using var context = new TestContext();
            using var handler = new ControlledHandler(
                async (self, token, call) =>
                {
                    if (call == 1)
                    {
                        await self.Release.Task;
                        if (response == 0)
                        {
                            throw new HttpRequestException(
                                HttpRequestError.NameResolutionError,
                                "synthetic DNS unavailable",
                                new SocketException((int)SocketError.HostNotFound)
                            );
                        }

                        return new HttpResponseMessage((HttpStatusCode)response);
                    }

                    return new HttpResponseMessage(HttpStatusCode.OK);
                }
            );
            using var client = new HttpClient(handler);
            using var service = context.Create(httpClient: client);
            Check(service.SetEnabled(true), "Enabled.");
            service.Track(AnalyticsEvent.AppOpened);
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Promptly(() => service.Track(AnalyticsEvent.GifSelected), "Tracking during offline delivery returns immediately.");
            handler.Release.TrySetResult();
            await service.ShutdownAsync();
            Check(handler.Requests.Count == 2, "A rejected request is dropped once and does not end the worker.");
        }
    }

    private static async Task SynchronousTransportAsync()
    {
        using var context = new TestContext();
        using var handler = new ControlledHandler(
            (self, token, call) =>
            {
                self.TransportGate.Wait();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        );
        using var client = new HttpClient(handler);
        using var service = context.Create(httpClient: client);
        Check(service.SetEnabled(true), "Enabled.");
        service.Track(AnalyticsEvent.AppOpened);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            await Promptly(
                () =>
                {
                    for (var index = 0; index < 10_000; index++)
                    {
                        service.Track(AnalyticsEvent.GifSelected);
                    }

                    Check(service.IsEnabled && service.HasSavedPreference, "Preference getters stay responsive.");
                },
                "Capture stays responsive while SendAsync blocks before returning a Task."
            );
            await Promptly(() => Check(service.SetEnabled(false), "Opt-out saves."), "Opt-out never waits for transport startup.");
            await Promptly(
                () =>
                {
                    service.Stop();
                    service.Dispose();
                },
                "Stop and Dispose never wait for transport startup."
            );
        }
        finally
        {
            handler.TransportGate.Set();
        }

        await service.ShutdownAsync();
        Check(handler.Requests.Count == 1, "Queued events cannot escape after opt-out and stop.");
    }

    private static async Task UncooperativeTransportAsync()
    {
        using var context = new TestContext();
        using var handler = new ControlledHandler(
            async (self, token, call) =>
            {
                await self.Release.Task; // Deliberately ignore cancellation, like a broken transport.
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        );
        using var client = new HttpClient(handler);
        using var service = context.Create(httpClient: client);
        Check(service.SetEnabled(true), "Enabled.");
        service.Track(AnalyticsEvent.AppOpened);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            service.Track(AnalyticsEvent.GifSelected);
            var timer = Stopwatch.StartNew();
            await service.ShutdownAsync();
            Check(timer.Elapsed < TimeSpan.FromSeconds(2), "Best-effort drain is bounded even when the transport ignores cancellation.");
            await Promptly(
                () =>
                {
                    service.Stop();
                    service.Dispose();
                },
                "Stop and Dispose do not await an uncancellable request."
            );
        }
        finally
        {
            handler.Release.TrySetResult();
        }

        Check(handler.Requests.Count == 1, "Timed-out drain discards pending requests.");
    }

    private static async Task BlockingCallbacksAsync()
    {
        using var context = new TestContext();
        using var handler = new ControlledHandler(
            async (self, token, call) =>
            {
                using var registration = token.Register(() =>
                {
                    self.CancellationStarted.TrySetResult();
                    self.CancellationGate.Wait();
                });
                self.RegistrationReady.TrySetResult();
                await self.Release.Task;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        );
        using var client = new HttpClient(handler);
        using var service = context.Create(httpClient: client);
        Check(service.SetEnabled(true), "Enabled.");
        service.Track(AnalyticsEvent.AppOpened);
        await handler.RegistrationReady.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            service.Track(AnalyticsEvent.GifSelected);
            await Promptly(
                () => Check(service.SetEnabled(false), "Opt-out saves."),
                "Opt-out never runs a blocked transport cancellation callback inline."
            );
            await handler.CancellationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Promptly(
                () =>
                {
                    service.Track(AnalyticsEvent.PageViewed);
                    service.Stop();
                    service.Dispose();
                },
                "Track, Stop, and Dispose stay responsive while cancellation callbacks block."
            );
        }
        finally
        {
            handler.CancellationGate.Set();
            handler.Release.TrySetResult();
        }

        await service.ShutdownAsync();
        Check(handler.Requests.Count == 1, "Cancellation callback stalls do not revive pending events.");
    }

    private static async Task ConcurrentCaptureAsync()
    {
        using var context = new TestContext(blockFirst: true);
        using var service = context.Create();
        Check(service.SetEnabled(true), "Enabled.");
        service.Track(AnalyticsEvent.AppOpened);
        await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.WhenAll(
                Enumerable
                    .Range(0, 8)
                    .Select(_ =>
                        Task.Run(() =>
                        {
                            for (var index = 0; index < 5000; index++)
                            {
                                service.Track(AnalyticsEvent.GifSelected);
                            }
                        })
                    )
            )
            .WaitAsync(TimeSpan.FromSeconds(2));
        context.Handler.Release.TrySetResult();
        await service.ShutdownAsync();
        Check(context.Handler.Requests.Count is >= 1 and <= 65, "Concurrent capture cannot exceed the 64-entry queue and active request.");
        await Promptly(service.Stop, "Stopping after overload remains immediate.");
    }

    private static async Task Promptly(Action action, string message)
    {
        var elapsed = await Task.Run(() =>
            {
                var timer = Stopwatch.StartNew();
                action();
                return timer.Elapsed;
            })
            .WaitAsync(TimeSpan.FromSeconds(2));
        Check(elapsed < TimeSpan.FromMilliseconds(500), message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal sealed class ManualTimeProvider(DateTimeOffset initial) : TimeProvider
{
    private DateTimeOffset _now = initial;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan duration) => _now += duration;

    public void Set(DateTimeOffset timestamp) => _now = timestamp;
}

internal sealed class ControlledHandler(Func<ControlledHandler, CancellationToken, int, Task<HttpResponseMessage>> behavior)
    : HttpMessageHandler
{
    private int _calls;
    public ConcurrentQueue<CapturedRequest> Requests { get; } = new();
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource RegistrationReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CancellationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ManualResetEventSlim TransportGate { get; } = new();
    public ManualResetEventSlim CancellationGate { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // ByteArrayContent is already in memory; the fake handler deliberately does no actual network I/O.
        var raw = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        Requests.Enqueue(new CapturedRequest(raw, request.RequestUri!.AbsoluteUri, request.Content.Headers.ContentType!.MediaType!));
        var number = Interlocked.Increment(ref _calls);
        Started.TrySetResult();
        return behavior(this, cancellationToken, number);
    }

    protected override void Dispose(bool disposing)
    {
        // Always release synthetic blockers, including when an assertion failed.
        TransportGate.Set();
        CancellationGate.Set();
        Release.TrySetResult();
        base.Dispose(disposing);
    }
}
