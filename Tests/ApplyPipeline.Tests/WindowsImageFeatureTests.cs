using System.Text.Json;
using LockscreenGif.Models;
using LockscreenGif.Privileged;
using LockscreenGif.Services.Lockscreen;

internal static class WindowsImageFeatureTests
{
    public static async Task EnabledFeatureIsReadOnly()
    {
        var reads = 0;
        var state = State(2, 2, 0);
        var session = new PermissionOnlySession();
        var events = new List<LockscreenApplyEvent>();
        CacheFixture? fixture = null;
        using (
            fixture = new CacheFixture(
                readFeature: () =>
                {
                    reads++;
                    Check(Directory.GetFiles(fixture!.Folder).Length == 0, "The feature must be observed before any cache writes.");
                    return state;
                },
                permissionSession: session
            )
        )
        {
            var result = await fixture.Apply(events.Add);
            Check(
                result.Success && reads == 1 && result.WindowsImageFeatureAtApply == state,
                "An enabled feature must be captured exactly once without stopping GIF copying."
            );
            Check(session.Calls == 0, "Readable cache files and a known enabled feature must not request any helper operation.");
            Check(state.RuntimeState == 2 && state.OverrideState == 2, "Applying must not change the current or pending feature state.");
            Check(
                events.Count(e => e.Stage == "WindowsImageFeature") == 1 && events.Any(e => e.Message.Contains("RuntimeState=Enabled(2)")),
                "The exact read-only feature observation must be logged."
            );
            Check(
                events.Last().Stage == "Completed" && !events.Last().Message.Contains("Restart", StringComparison.OrdinalIgnoreCase),
                "Apply completion must report file verification without prescribing a feature change."
            );
        }
    }

    public static async Task UnavailableFeatureStillApplies()
    {
        foreach (
            var state in new[]
            {
                new WindowsImageFeatureState
                {
                    ObservedAt = DateTimeOffset.UtcNow,
                    QueryStatus = unchecked((int)0xC0000001),
                    QueryError = "Synthetic native failure",
                },
                new WindowsImageFeatureState
                {
                    ObservedAt = DateTimeOffset.UtcNow,
                    QueryStatus = 0,
                    RuntimeState = 2,
                    OverrideError = "Synthetic registry read failure",
                },
                State(0),
            }
        )
        {
            using var fixture = new CacheFixture(readFeature: () => state);
            var result = await fixture.Apply();
            Check(
                result.Success && result.WindowsImageFeatureAtApply == state,
                "Missing, default, or unreadable feature data must remain evidence and must not block copying."
            );
        }
        var reads = 0;
        using var throwing = new CacheFixture(readFeature: () =>
        {
            reads++;
            throw new IOException("Synthetic reader exception");
        });
        var failedRead = await throwing.Apply();
        Check(
            failedRead.Success
                && reads == 1
                && failedRead.WindowsImageFeatureAtApply?.QueryError?.Contains("Synthetic reader exception") == true,
            "Unexpected reader failures must be captured without aborting a valid GIF apply."
        );
        Check(failedRead.WindowsImageFeatureAtApply!.ObservedAt != default, "Unavailable evidence must retain its observation time.");
    }

    public static async Task InvalidAndCancelledDoNotRead()
    {
        var reads = 0;
        using var fixture = new CacheFixture(readFeature: () =>
        {
            reads++;
            return State(2);
        });
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var cancelled = await fixture.Apply(token: cancel.Token);
        Check(
            cancelled.Cancelled && reads == 0 && cancelled.WindowsImageFeatureAtApply is null,
            "A precancelled apply must not query feature settings."
        );
        await File.WriteAllTextAsync(fixture.Source, "not a GIF file");
        var invalid = await fixture.Apply();
        Check(
            !invalid.Success && reads == 0 && invalid.WindowsImageFeatureAtApply is null,
            "An invalid GIF must fail before querying feature settings."
        );
    }

    public static async Task CopyFailureRetainsSnapshot()
    {
        var state = State(2, 1, 0);
        using var fixture = new CacheFixture(readFeature: () => state);
        await File.WriteAllTextAsync(fixture.MainImage, "previous image");
        await using var locked = new FileStream(fixture.MainImage, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = await fixture.Apply();
        Check(
            !result.Success && result.WindowsImageFeatureAtApply == state && result.Files.Any(file => file.Verified),
            "Partial copy failure must preserve the apply-time feature snapshot."
        );
        var json = JsonSerializer.Serialize(result);
        var clone = JsonSerializer.Deserialize<LockscreenApplyResult>(json)!;
        Check(
            clone.WindowsImageFeatureAtApply?.RuntimeState == 2
                && clone.WindowsImageFeatureAtApply.OverrideState == 1
                && clone.WindowsImageFeatureAtApply.ObservedAt == state.ObservedAt,
            "Report JSON must retain observed runtime, pending override, and observation time."
        );
        Check(
            !json.Contains("ChangeAttempted", StringComparison.Ordinal) && !json.Contains("DesiredState", StringComparison.Ordinal),
            "The apply record must contain an observation rather than a feature-change result."
        );
    }

    public static async Task CancellationPreservesSnapshot()
    {
        using var cancel = new CancellationTokenSource();
        var state = State(2);
        using var fixture = new CacheFixture(readFeature: () =>
        {
            cancel.Cancel();
            return state;
        });
        var result = await fixture.Apply(token: cancel.Token);
        Check(
            result.Cancelled && result.WindowsImageFeatureAtApply == state && result.Files.Count == 0,
            "Cancellation following the read must preserve the snapshot and prevent cache writes."
        );
    }

    private static WindowsImageFeatureState State(uint runtime, int? persisted = null, int? options = null) =>
        new()
        {
            ObservedAt = DateTimeOffset.UtcNow,
            QueryStatus = 0,
            RuntimeState = runtime,
            RuntimePriority = 0,
            OverrideExists = persisted.HasValue,
            OverrideState = persisted,
            OverrideOptions = options,
        };

    private static void Check(bool value, string message) => PermissionSessionTests.Check(value, message);

    // Applying receives cache-access capability only. It must not require or discover
    // a privileged feature-action capability when the observed feature is enabled.
    private sealed class PermissionOnlySession : ICachePermissionSession
    {
        public int Calls { get; private set; }

        public Task<int> GrantAsync(string path, bool write, CancellationToken token)
        {
            Calls++;
            throw new InvalidOperationException("Unexpected helper request during readable-cache apply.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
