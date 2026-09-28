using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using LockscreenGif.Services.Analytics;

await AnalyticsTests.RunAsync();

internal static class AnalyticsTests
{
    public static async Task RunAsync()
    {
        await Run("uninitialized, opted out, delivery disabled, and invalid configuration never send", DisabledAsync);
        await Run("development and production use separate configured tokens and typed environment markers", EnvironmentsAsync);
        await Run(
            "the shared analytics preference preserves opt-out across development and production builds",
            EnvironmentPreferencesAsync
        );
        await Run("saved choices persist and opt-out removes and rotates identifiers", PreferencesAsync);
        await Run("payload has only allowed fields and no error text", PayloadAsync);
        await Run("malformed and inaccessible settings fail closed without first-run reset", InvalidPreferencesAsync);
        await Run("failed preference writes stop collection", FailedWriteAsync);
        await Run("opt-out cancels active delivery and discards stale queued events", OptOutAsync);
        await OptOutTests.RunAsync();
        await Run("the memory queue stays bounded under overload", BoundedQueueAsync);
        await Run("transport errors do not prevent subsequent delivery", FailureIsolationAsync);
        await Run("shutdown drains a healthy queue and bounds a stalled transport", ShutdownAsync);
        await ReliabilityTests.RunAsync();
        await WindowsContextTests.RunAsync();
        await GenerationErrorTests.RunAsync();
        await ErrorTrackingTests.RunAsync();
        await ApplySourceTests.RunAsync();
        await ApplyFailureTests.RunAsync();
        Console.WriteLine("All analytics checks passed. Every request used a fake HTTP transport.");
    }

    private static async Task Run(string name, Func<Task> test)
    {
        await test();
        Console.WriteLine("PASS " + name);
    }

    private static async Task DisabledAsync()
    {
        using var context = new TestContext();
        using (var service = context.Create())
        {
            Check(!service.IsEnabled && !service.HasSavedPreference, "A missing preference starts uninitialized and off.");
            service.Track(AnalyticsEvent.AppOpened);
            Check(!File.Exists(context.PreferencesPath), "Tracking without a saved preference creates no identifier or file.");
            await service.ShutdownAsync();
        }

        using (var service = context.Create(allowSending: false))
        {
            Check(service.SetEnabled(true) && service.IsEnabled, "Delivery disabled still saves the user's preference.");
            service.Track(AnalyticsEvent.AppOpened);
            await service.ShutdownAsync();
        }

        foreach (
            var host in new[]
            {
                "http://eu.i.posthog.com",
                "https://eu.i.posthog.com.attacker.invalid",
                "https://localhost",
                "https://eu.i.posthog.com:444",
                "https://name@eu.i.posthog.com",
                "https://eu.i.posthog.com/path",
                "https://eu.i.posthog.com/?token=example",
                "https://eu.i.posthog.com/#fragment",
            }
        )
        {
            using var service = context.Create(options: new AnalyticsOptions { ProjectToken = "phc_test_only", Host = host });
            Check(!service.IsConfigured, "Unsafe ingestion host rejected.");
            service.Track(AnalyticsEvent.AppOpened);
            await service.ShutdownAsync();
        }

        using (var service = context.Create(options: new AnalyticsOptions()))
        {
            Check(!service.IsConfigured, "Missing project token is not configured.");
            service.Track(AnalyticsEvent.AppOpened);
            await service.ShutdownAsync();
        }

        using (
            var service = context.Create(
                options: new AnalyticsOptions { ProjectToken = "phc_test_only", Environment = (AnalyticsEnvironment)999 }
            )
        )
        {
            Check(!service.IsConfigured, "An unknown analytics environment fails closed.");
            service.Track(AnalyticsEvent.AppOpened);
            await service.ShutdownAsync();
        }

        Check(context.Handler.Requests.Count == 0, "No request was attempted without effective collection enabled.");
    }

    private static async Task EnvironmentsAsync()
    {
        using var context = new TestContext();
        const string productionToken = "phc_synthetic_production";
        const string developmentToken = "phc_synthetic_development";
        const string host = "https://eu.i.posthog.com";
        var development = AnalyticsOptions.ForBuild(false, productionToken, developmentToken, host);
        var production = AnalyticsOptions.ForBuild(true, productionToken, developmentToken, host);
        Check(new AnalyticsOptions().Environment == AnalyticsEnvironment.Development, "Configuration defaults to development.");
        Check(
            development.Environment == AnalyticsEnvironment.Development,
            "Every non-GitHub build selects development, regardless of Debug or Release."
        );
        Check(production.Environment == AnalyticsEnvironment.Production, "Only a GitHub Actions build selects production.");
        foreach (var options in new[] { development, production })
        {
            using var service = context.Create(options: options);
            Check(service.SetEnabled(true), "Environment preference saved.");
            service.Track(AnalyticsEvent.AppOpened);
            await service.ShutdownAsync();
        }

        var requests = context.Handler.Requests.ToArray();
        Check(requests.Length == 2, "Both enabled environments send through the fake transport.");
        Check(
            requests[0].Root.GetProperty("api_key").GetString() == "phc_synthetic_development",
            "Development uses its configured project token."
        );
        Check(requests[0].Properties.GetProperty("environment").GetString() == "development", "Development marker is a stable literal.");
        Check(
            requests[1].Root.GetProperty("api_key").GetString() == "phc_synthetic_production",
            "Production uses its configured project token."
        );
        Check(requests[1].Properties.GetProperty("environment").GetString() == "production", "Production marker is a stable literal.");
        Check(
            requests[0].Properties.GetProperty("distinct_id").GetString() == requests[1].Properties.GetProperty("distinct_id").GetString(),
            "Switching build environments preserves the shared installation preference."
        );

        using var unconfigured = context.Create(options: AnalyticsOptions.ForBuild(false, productionToken, "", host));
        Check(!unconfigured.IsConfigured, "A missing development token never falls back to the production token.");
        unconfigured.Track(AnalyticsEvent.AppOpened);
        await unconfigured.ShutdownAsync();
        Check(context.Handler.Requests.Count == 2, "A non-GitHub build without a development token sends nothing.");
    }

    private static async Task EnvironmentPreferencesAsync()
    {
        using var context = new TestContext();
        using (var development = context.Create())
        {
            Check(development.SetEnabled(false), "Development opt-out saves.");
            await development.ShutdownAsync();
        }

        foreach (var isGitHubActionsBuild in new[] { true, false })
        {
            var options = AnalyticsOptions.ForBuild(
                isGitHubActionsBuild,
                "phc_synthetic_production",
                "phc_synthetic_development",
                "https://eu.i.posthog.com"
            );
            using var service = context.Create(options: options);
            Check(!service.IsEnabled && service.HasSavedPreference, "The saved opt-out survives either build environment.");
            service.Track(AnalyticsEvent.AppOpened);
            await service.ShutdownAsync();
        }

        Check(context.Handler.Requests.Count == 0, "Switching build environment never overrides opt-out.");
    }

    private static async Task PreferencesAsync()
    {
        using var context = new TestContext();
        string firstId;
        using (var service = context.Create())
        {
            Check(service.SetEnabled(true), "Enabling saves preference.");
            service.Track(AnalyticsEvent.AppOpened);
            await service.ShutdownAsync();
            firstId = context.Handler.Requests.Single().Properties.GetProperty("distinct_id").GetString()!;
            Check(Guid.TryParse(firstId, out _), "Installation identifier is random UUID.");
        }

        using (var service = context.Create())
        {
            Check(service.IsEnabled && service.HasSavedPreference, "Saved enabled state is restored.");
            service.Track(AnalyticsEvent.AppOpened);
            await service.ShutdownAsync();
            var requests = context.Handler.Requests.ToArray();
            Check(requests[1].Properties.GetProperty("distinct_id").GetString() == firstId, "Installation ID persists across launches.");
            Check(
                requests[0].Properties.GetProperty("$session_id").GetString()
                    != requests[1].Properties.GetProperty("$session_id").GetString(),
                "A new launch receives a new session ID."
            );
        }

        using (var service = context.Create())
        {
            Check(service.SetEnabled(false), "Disabling saves preference.");
            Check(!service.IsEnabled && service.HasSavedPreference, "Disabled is a saved preference.");
            using var settings = JsonDocument.Parse(File.ReadAllText(context.PreferencesPath));
            Check(settings.RootElement.GetProperty("InstallationId").ValueKind == JsonValueKind.Null, "Opt-out removes the persisted ID.");
            service.Track(AnalyticsEvent.AppOpened);
            await service.ShutdownAsync();
        }

        using (var service = context.Create())
        {
            Check(!service.IsEnabled && service.HasSavedPreference, "A saved opt-out must never become first-run default-on.");
            Check(service.SetEnabled(true), "Re-enabling saves preference.");
            service.Track(AnalyticsEvent.AppOpened);
            await service.ShutdownAsync();
            Check(
                context.Handler.Requests.Last().Properties.GetProperty("distinct_id").GetString() != firstId,
                "Re-enabling rotates the ID."
            );
        }

        Check(context.Handler.Requests.Count == 4, "Only the final opt-out event is sent for the disabled transition.");
    }

    private static async Task PayloadAsync()
    {
        using var context = new TestContext(blockFirst: true);
        using var service = context.Create();
        Check(service.SetEnabled(true), "Preference saved.");
        var operationId = Guid.NewGuid();
        service.Track(
            AnalyticsEvent.LockscreenApplyCompleted,
            new AnalyticsProperties
            {
                Outcome = AnalyticsOutcome.Partial,
                Page = AnalyticsPage.Settings,
                DurationMs = 125.7,
                TargetCount = 3,
                CopiedCount = 2,
                VerifiedCount = 1,
                FailedCount = 1,
                ApiRequested = true,
                ApiCompleted = false,
                ErrorKind = AnalyticsProperties.ClassifyError(new UnauthorizedAccessException("synthetic-private-path")),
                OperationId = operationId,
                Workflow = AnalyticsWorkflow.Diagnostics,
                OutputWidth = 1280,
                TargetFps = 0,
                ClipDurationSeconds = 3.5,
                SelectedFrameCount = 120,
                UsesReferenceGif = false,
                GifSizeBytes = 5_000_000_000L,
                GifWidth = 1920,
                GifHeight = 1080,
            }
        );
        await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        service.Track(
            AnalyticsEvent.AppError,
            new AnalyticsProperties
            {
                DurationMs = double.NaN,
                TargetCount = -1,
                Outcome = (AnalyticsOutcome)999,
                Page = (AnalyticsPage)999,
                OperationId = Guid.Empty,
                Workflow = (AnalyticsWorkflow)999,
                OutputWidth = 32769,
                TargetFps = double.PositiveInfinity,
                ClipDurationSeconds = double.NaN,
                SelectedFrameCount = 10_000_001,
                GifSizeBytes = long.MaxValue,
                GifWidth = 0,
                GifHeight = 65536,
            }
        );
        service.Track((AnalyticsEvent)999);
        context.Handler.Release.TrySetResult();
        await service.ShutdownAsync();

        var request = context.Handler.Requests.First();
        var root = request.Root;
        Check(request.Url == "https://eu.i.posthog.com/capture/" && request.ContentType == "application/json", "EU JSON capture endpoint.");
        Check(root.GetProperty("event").GetString() == "lockscreen_apply_completed", "Event names use the stable allowlist.");
        Check(
            root.GetProperty("distinct_id").GetString() == request.Properties.GetProperty("distinct_id").GetString(),
            "Distinct ID is explicit."
        );
        Check(DateTimeOffset.TryParse(root.GetProperty("timestamp").GetString(), out _), "Timestamp is ISO 8601.");
        Check(Guid.TryParse(root.GetProperty("uuid").GetString(), out _), "Each captured event receives a UUID for deduplication.");
        var expected = new HashSet<string>
        {
            "distinct_id",
            "$session_id",
            "app_version",
            "windows_version",
            "platform",
            "environment",
            "$process_person_profile",
            "$geoip_disable",
            "outcome",
            "page",
            "duration_ms",
            "target_count",
            "copied_count",
            "verified_count",
            "failed_count",
            "api_requested",
            "api_completed",
            "error_kind",
            "operation_id",
            "workflow",
            "requested_width",
            "requested_fps",
            "requested_fps_mode",
            "clip_duration_seconds",
            "selected_frame_count",
            "uses_reference_gif",
            "gif_size_bytes",
            "gif_width",
            "gif_height",
        };
        Check(
            expected.SetEquals(request.Properties.EnumerateObject().Select(property => property.Name)),
            "Payload matches exactly the approved fields."
        );
        Check(!request.Raw.Contains("synthetic-private-path", StringComparison.Ordinal), "Exception messages never enter payloads.");
        Check(!request.Properties.GetProperty("$process_person_profile").GetBoolean(), "Person profiles disabled.");
        Check(request.Properties.GetProperty("$geoip_disable").GetBoolean(), "GeoIP enrichment disabled.");
        Check(request.Properties.GetProperty("$session_id").GetString()![14] == '7', "Session uses PostHog-compatible UUIDv7.");
        Check(request.Properties.GetProperty("duration_ms").GetDouble() == 126, "Duration is rounded to milliseconds.");
        Check(request.Properties.GetProperty("error_kind").GetString() == "permission_denied", "Errors use coarse categories.");
        Check(request.Properties.GetProperty("operation_id").GetGuid() == operationId, "The random operation ID is retained.");
        Check(request.Properties.GetProperty("workflow").GetString() == "diagnostics", "Workflow uses a stable allowlisted value.");
        Check(
            request.Properties.GetProperty("requested_fps").GetDouble() == 0,
            "Zero requested FPS retains the all-source-frames setting."
        );
        Check(!request.Properties.GetProperty("uses_reference_gif").GetBoolean(), "False options remain measurable.");
        Check(request.Properties.GetProperty("gif_size_bytes").GetInt64() == 5_000_000_000L, "GIF bytes retain 64-bit precision.");
        Check(
            request.Properties.GetProperty("gif_width").GetInt32() == 1920
                && request.Properties.GetProperty("gif_height").GetInt32() == 1080,
            "GIF dimensions use separate pixel properties."
        );
        Check(context.Handler.Requests.Last().Properties.EnumerateObject().Count() == 8, "Invalid numeric and enum values are dropped.");
        Check(context.Handler.Requests.Count == 2, "Unknown events are dropped.");
    }

    private static async Task InvalidPreferencesAsync()
    {
        using var context = new TestContext();
        foreach (
            var content in new[]
            {
                "{",
                "{}",
                "{\"Version\":1}",
                "{\"Version\":1,\"Enabled\":true,\"InstallationId\":\"name\"}",
                new string('x', 4097),
            }
        )
        {
            File.WriteAllText(context.PreferencesPath, content);
            using var service = context.Create();
            Check(
                service.HasSavedPreference && !service.IsEnabled,
                "Malformed preferences are saved-state failures, never fresh installs."
            );
            service.Track(AnalyticsEvent.AppOpened);
            await service.ShutdownAsync();
        }

        using (var locked = new FileStream(context.PreferencesPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var service = context.Create())
        {
            Check(service.HasSavedPreference && !service.IsEnabled, "An inaccessible preference does not reset opt-out.");
            service.Track(AnalyticsEvent.AppOpened);
            await service.ShutdownAsync();
        }

        Check(context.Handler.Requests.Count == 0, "Settings failures cause no network traffic.");
    }

    private static async Task FailedWriteAsync()
    {
        using var context = new TestContext();
        using var service = context.Create();
        Check(service.SetEnabled(true), "Initial settings saved.");
        using var locked = new FileStream(context.PreferencesPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Check(!service.SetEnabled(false), "Failed persistence is reported to the caller.");
        Check(!service.IsEnabled && service.HasSavedPreference, "Runtime collection stops even when preference write fails.");
        service.Track(AnalyticsEvent.AppOpened);
        await service.ShutdownAsync();
        Check(
            context.Handler.Requests.Single().Root.GetProperty("event").GetString() == "analytics_opted_out",
            "The runtime opt-out is recorded even when persistence fails; no subsequent usage is sent."
        );
        Check(Directory.GetFiles(context.DirectoryPath, "*.tmp").Length == 0, "Failed writes leave no temporary ID file.");
    }

    private static async Task OptOutAsync()
    {
        using var context = new TestContext(blockFirst: true);
        using var service = context.Create();
        Check(service.SetEnabled(true), "Enabled.");
        service.Track(AnalyticsEvent.AppOpened);
        await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var oldId = context.Handler.Requests.Single().Properties.GetProperty("distinct_id").GetString();
        for (var index = 0; index < 10; index++)
        {
            service.Track(AnalyticsEvent.GifSelected);
        }

        Check(service.SetEnabled(false), "Opt-out saves.");
        await context.Handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        service.Track(AnalyticsEvent.VideoLoadCompleted);
        Check(service.SetEnabled(true), "Re-enabled.");
        service.Track(AnalyticsEvent.PageViewed);
        await service.ShutdownAsync();
        Check(context.Handler.Requests.Count == 3, "Only the old in-flight request, final opt-out, and newly enabled event were started.");
        foreach (var current in context.Handler.Requests.Skip(1))
        {
            if (current.Root.GetProperty("event").GetString() == "analytics_opted_out")
            {
                Check(
                    current.Properties.GetProperty("distinct_id").GetString() == oldId,
                    "The final opt-out uses the outgoing identifier."
                );
                continue;
            }
            Check(current.Root.GetProperty("event").GetString() == "page_viewed", "No pre-opt-out queued event survives.");
            Check(current.Properties.GetProperty("distinct_id").GetString() != oldId, "No old identifier survives re-enabling.");
        }
    }

    private static async Task BoundedQueueAsync()
    {
        using var context = new TestContext(blockFirst: true);
        using var service = context.Create();
        Check(service.SetEnabled(true), "Enabled.");
        service.Track(AnalyticsEvent.AppOpened);
        await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (var index = 0; index < 10_000; index++)
        {
            service.Track(AnalyticsEvent.GifSelected);
        }

        context.Handler.Release.TrySetResult();
        await service.ShutdownAsync();
        Check(context.Handler.Requests.Count == 65, "Overload retains at most 64 queued events plus one active request.");
    }

    private static async Task FailureIsolationAsync()
    {
        using var context = new TestContext(blockFirst: true, failFirst: true);
        using var service = context.Create();
        Check(service.SetEnabled(true), "Enabled.");
        service.Track(AnalyticsEvent.AppOpened);
        await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        service.Track(AnalyticsEvent.PageViewed);
        context.Handler.Release.TrySetResult();
        await service.ShutdownAsync();
        Check(context.Handler.Requests.Count == 2, "A failed request does not end the worker or retry indefinitely.");
    }

    private static async Task ShutdownAsync()
    {
        using var context = new TestContext(blockFirst: true);
        using var service = context.Create();
        Check(service.SetEnabled(true), "Enabled.");
        service.Track(AnalyticsEvent.AppOpened);
        await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        service.Track(AnalyticsEvent.GifSelected);
        var timer = Stopwatch.StartNew();
        await service.ShutdownAsync();
        Check(timer.Elapsed < TimeSpan.FromSeconds(2), "Shutdown returns within two seconds.");
        await context.Handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        service.Track(AnalyticsEvent.PageViewed);
        await service.ShutdownAsync();
        Check(context.Handler.Requests.Count == 1, "Shutdown cancels HTTP delivery and discards remaining events on timeout.");
        Check(!service.SetEnabled(true), "The service cannot restart after shutdown.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal sealed class TestContext : IDisposable
{
    public string DirectoryPath { get; } =
        Path.Combine(Path.GetTempPath(), "LockscreenGif-analytics-tests-" + Guid.NewGuid().ToString("N"));
    public string PreferencesPath => Path.Combine(DirectoryPath, "analytics.json");
    public RecordingHandler Handler { get; }
    private readonly HttpClient _client;

    public TestContext(bool blockFirst = false, bool failFirst = false)
    {
        Directory.CreateDirectory(DirectoryPath);
        Handler = new RecordingHandler(blockFirst, failFirst);
        _client = new HttpClient(Handler);
    }

    public AnalyticsService Create(
        bool allowSending = true,
        AnalyticsOptions? options = null,
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null,
        Func<AnalyticsWindowsContext>? windowsContext = null
    ) =>
        new(
            options ?? new AnalyticsOptions { ProjectToken = "phc_test_only" },
            PreferencesPath,
            "1.2.3-test",
            "10.0.26100",
            allowSending,
            httpClient ?? _client,
            timeProvider,
            windowsContext ?? (() => new AnalyticsWindowsContext())
        );

    public void Dispose()
    {
        _client.Dispose();
        // This unique directory is owned only by this synthetic test fixture.
        Directory.Delete(DirectoryPath, recursive: true);
    }
}

internal sealed class RecordingHandler(bool blockFirst, bool failFirst) : HttpMessageHandler
{
    private int _calls;
    public ConcurrentQueue<CapturedRequest> Requests { get; } = new();
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var number = Interlocked.Increment(ref _calls);
        var raw = await request.Content!.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue(new CapturedRequest(raw, request.RequestUri!.AbsoluteUri, request.Content.Headers.ContentType!.MediaType!));
        Started.TrySetResult();
        if (number == 1 && blockFirst)
        {
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }

        if (number == 1 && failFirst)
        {
            throw new HttpRequestException("synthetic-private-transport-detail");
        }

        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}

internal sealed record CapturedRequest(string Raw, string Url, string ContentType)
{
    public JsonElement Root => JsonSerializer.Deserialize<JsonElement>(Raw);
    public JsonElement Properties => Root.GetProperty("properties");
}
