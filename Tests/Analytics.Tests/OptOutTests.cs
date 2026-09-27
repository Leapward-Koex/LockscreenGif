using System.Diagnostics;
using System.Net;
using System.Text.Json;
using LockscreenGif.Services.Analytics;

internal static class OptOutTests
{
    public static async Task RunAsync()
    {
        await TransitionAsync();
        await DeliveryDisabledAsync();
        await ExpiredAsync();
        await FailuresAsync();
        Console.WriteLine("PASS final opt-out delivery is transition-only, bounded, routed, and best effort");
    }

    private static async Task TransitionAsync()
    {
        foreach (var production in new[] { false, true })
        {
            using var context = new TestContext(blockFirst: true);
            using var service = context.Create(
                options: AnalyticsOptions.ForBuild(production, "phc_test_prod", "phc_test_dev", "https://eu.i.posthog.com")
            );
            Check(service.SetEnabled(true), "Enable saved.");
            service.Track(AnalyticsEvent.AnalyticsOptedOut); // This reserved event cannot be manufactured by callers.
            service.Track(AnalyticsEvent.PageViewed);
            await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var first = context.Handler.Requests.Single();
            Check(first.Root.GetProperty("event").GetString() == "page_viewed", "Only transitions can capture the opt-out event.");
            for (var index = 0; index < 100; index++)
            {
                service.Track(AnalyticsEvent.GifSelected);
            }
            Check(service.SetEnabled(false) && !service.IsEnabled, "Opt-out immediately disables capture.");
            Check(service.SetEnabled(false), "Repeating opt-out still saves the preference.");
            service.Track(AnalyticsEvent.AppOpened);
            service.Track(AnalyticsEvent.AnalyticsOptedOut);
            await service.ShutdownAsync();
            Check(context.Handler.Requests.Count == 2, "Full queue is discarded in favor of exactly one final opt-out.");
            var final = context.Handler.Requests.Last();
            Check(final.Root.GetProperty("event").GetString() == "analytics_opted_out", "Final event has the documented name.");
            Check(
                final.Properties.GetProperty("distinct_id").GetString() == first.Properties.GetProperty("distinct_id").GetString()
                    && final.Properties.GetProperty("$session_id").GetString() == first.Properties.GetProperty("$session_id").GetString(),
                "Opt-out retains the outgoing installation and session IDs."
            );
            Check(final.Properties.EnumerateObject().Count() == 8, "Opt-out includes common allowlisted metadata only.");
            Check(
                final.Root.GetProperty("api_key").GetString() == (production ? "phc_test_prod" : "phc_test_dev"),
                "Final event respects the build environment."
            );
            using var settings = JsonDocument.Parse(File.ReadAllText(context.PreferencesPath));
            Check(!settings.RootElement.GetProperty("Enabled").GetBoolean(), "Disabled preference is persisted.");
            Check(settings.RootElement.GetProperty("InstallationId").ValueKind == JsonValueKind.Null, "Local identifier is removed.");
            using var restarted = context.Create();
            Check(restarted.SetEnabled(false), "Repeated opt-out after restart saves.");
            await restarted.ShutdownAsync();
            Check(context.Handler.Requests.Count == 2, "Restart with sharing off sends no final event again.");
        }
    }

    private static async Task DeliveryDisabledAsync()
    {
        using var context = new TestContext();
        using (var service = context.Create(allowSending: false))
        {
            Check(service.SetEnabled(true) && service.SetEnabled(false), "Preferences work when sending is disabled.");
            await service.ShutdownAsync();
        }
        using (var service = context.Create(options: new AnalyticsOptions()))
        {
            Check(service.SetEnabled(true) && service.SetEnabled(false), "Preferences work without configuration.");
            await service.ShutdownAsync();
        }
        Check(context.Handler.Requests.IsEmpty, "Opt-out never bypasses configuration or the delivery gate.");
    }

    private static async Task ExpiredAsync()
    {
        using var context = new TestContext();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var handler = new ControlledHandler(
            async (self, token, call) =>
            {
                await self.Release.Task; // Hold the worker even after its old request is cancelled.
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        );
        using var client = new HttpClient(handler);
        using var service = context.Create(httpClient: client, timeProvider: clock);
        Check(service.SetEnabled(true), "Enabled.");
        service.Track(AnalyticsEvent.PageViewed);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(service.SetEnabled(false), "Opt-out saves while the worker is occupied.");
        clock.Advance(TimeSpan.FromSeconds(4));
        handler.Release.TrySetResult();
        await service.ShutdownAsync();
        Check(handler.Requests.Count == 1, "An expired final event is dropped instead of sending later.");
    }

    private static async Task FailuresAsync()
    {
        foreach (var mode in new[] { "offline", "rejected", "slow" })
        {
            using var context = new TestContext();
            using var handler = new ControlledHandler(
                async (self, token, call) =>
                {
                    if (mode == "offline")
                    {
                        throw new HttpRequestException("synthetic offline transport");
                    }
                    if (mode == "slow")
                    {
                        try
                        {
                            await Task.Delay(Timeout.InfiniteTimeSpan, token);
                        }
                        catch (OperationCanceledException)
                        {
                            self.CancellationStarted.TrySetResult();
                            throw;
                        }
                    }
                    return new HttpResponseMessage(HttpStatusCode.Forbidden);
                }
            );
            using var client = new HttpClient(handler);
            using var service = context.Create(httpClient: client);
            Check(service.SetEnabled(true), "Enabled.");
            var timer = Stopwatch.StartNew();
            Check(service.SetEnabled(false), "Opt-out saves regardless of delivery.");
            Check(timer.Elapsed < TimeSpan.FromMilliseconds(500) && !service.IsEnabled, "Opt-out does not await the network.");
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (mode == "slow")
            {
                await handler.CancellationStarted.Task.WaitAsync(TimeSpan.FromSeconds(4));
            }
            service.Track(AnalyticsEvent.PageViewed);
            await service.ShutdownAsync();
            Check(handler.Requests.Count == 1, "Failed opt-out delivery has no retry and cannot revive ordinary collection.");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
