using LockscreenGif.Privileged;
using LockscreenGif.Services.Lockscreen;

internal static class WindowsImageFeatureActionTests
{
    public static async Task ExplicitDirections()
    {
        var session = new Session();
        var created = 0;
        var before = State(2);
        var service = new WindowsImageFeatureService(
            () =>
            {
                created++;
                return session;
            },
            () => false,
            _ => before
        );
        var disabled = await service.SetEnabledAsync(false);
        before = State(1);
        var enabled = await service.SetEnabledAsync(true);
        Check(
            created == 2 && session.Directions.SequenceEqual([false, true]),
            "Each explicit button must request only its fixed direction."
        );
        Check(
            disabled.DesiredState == "Disabled" && enabled.DesiredState == "Enabled" && enabled.Outcome == "Enabled",
            "Actions retain desired state and result."
        );
        Check(session.Disposals == 2 && !service.IsBusy, "Each action owns and drains its helper lifetime.");
    }

    public static async Task NoopAndCancellation()
    {
        var created = 0;
        var before = State(1);
        var service = new WindowsImageFeatureService(
            () =>
            {
                created++;
                return new Session();
            },
            () => false,
            _ => before
        );
        var noop = await service.SetEnabledAsync(false);
        using var token = new CancellationTokenSource();
        token.Cancel();
        var cancelled = await service.SetEnabledAsync(true, token.Token);
        Check(
            created == 0 && noop.Outcome == "AlreadyDisabled" && cancelled.Outcome == "Cancelled",
            "No-op and precancellation cannot launch helpers."
        );
        Check(
            !cancelled.ChangeAttempted && !cancelled.ChangeOutcomeUnknown && !service.IsBusy,
            "Predispatch cancellation cannot report a mutation."
        );
    }

    public static async Task RefusesApplyOverlap()
    {
        var service = new WindowsImageFeatureService(() => throw new Exception("Unexpected launch"), () => true, _ => State(2));
        var result = await service.SetEnabledAsync(false);
        Check(result.Outcome == "Failed" && !result.ChangeAttempted, "An active apply must prevent a feature change.");
    }

    public static async Task SerializesAndDrains()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new Session { Wait = release.Task };
        var before = State(2);
        var service = new WindowsImageFeatureService(() => session, () => false, _ => before);
        var first = service.SetEnabledAsync(false);
        var idle = service.WaitForIdleAsync();
        using var cancelled = new CancellationTokenSource();
        var queued = service.SetEnabledAsync(true, cancelled.Token);
        cancelled.Cancel();
        var queuedResult = await queued;
        Check(
            service.IsBusy && !idle.IsCompleted && session.Directions.Count == 1,
            "Shutdown and queued actions wait for the dispatched action."
        );
        release.SetResult();
        var firstResult = await first;
        await idle;
        Check(
            firstResult.Outcome == "Disabled" && queuedResult.Outcome == "Cancelled" && !service.IsBusy,
            "Completing the action releases the lifetime gate."
        );
    }

    public static async Task PreDispatchFailureIsKnown()
    {
        var service = new WindowsImageFeatureService(() => throw new IOException("Synthetic launch failure"), () => false, _ => State(2));
        var result = await service.SetEnabledAsync(false);
        Check(
            result.Outcome == "Failed" && !result.ChangeAttempted && !result.ChangeOutcomeUnknown,
            "A launch failure is not an uncertain mutation."
        );
    }

    public static async Task SelectedIdIsCaptured()
    {
        const uint customId = 61653826;
        var selectedId = customId;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new Session { Wait = release.Task };
        var service = new WindowsImageFeatureService(() => session, () => false, id => State(2, id), () => selectedId);
        var action = service.SetEnabledAsync(false);
        selectedId = WindowsImageFeature.DefaultFeatureId;
        release.SetResult();
        var result = await action;
        Check(
            session.FeatureIds.SequenceEqual([customId])
                && result.FeatureId == customId
                && result.Before?.FeatureId == customId
                && result.After?.FeatureId == customId,
            "An action must retain its selected ID through dispatch and readback even if the preference later changes."
        );

        var mismatch = new WindowsImageFeatureService(
            () => throw new Exception("Unexpected launch"),
            () => false,
            _ => State(2),
            () => customId
        );
        var mismatchResult = await mismatch.SetEnabledAsync(false);
        Check(
            mismatchResult.Outcome == "Failed" && mismatchResult.FeatureId == customId && !mismatchResult.ChangeAttempted,
            "An observation for a different feature must never authorize a change."
        );

        var invalid = new WindowsImageFeatureService(
            () => throw new Exception("Unexpected launch"),
            () => false,
            _ => throw new Exception("Unexpected read"),
            () => 0
        );
        var invalidResult = await invalid.SetEnabledAsync(false);
        Check(
            invalidResult.Outcome == "Failed" && !invalidResult.ChangeAttempted,
            "An invalid ID is rejected before reading or launching a helper."
        );
    }

    private static WindowsImageFeatureState State(uint state, uint featureId = WindowsImageFeature.DefaultFeatureId) =>
        new()
        {
            FeatureId = featureId,
            ObservedAt = DateTimeOffset.UtcNow,
            QueryStatus = 0,
            RuntimeState = state,
            RuntimePriority = 0,
            OverrideExists = false,
        };

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class Session : IPrivilegedOperationSession
    {
        internal List<bool> Directions { get; } = [];
        internal List<uint> FeatureIds { get; } = [];
        internal int Disposals { get; private set; }
        internal Task? Wait { get; init; }

        public Task<WindowsImageFeatureResult> EnableWindowsImageFeatureAsync(
            CancellationToken token,
            uint featureId = WindowsImageFeature.DefaultFeatureId
        ) => Set(true, featureId);

        public Task<WindowsImageFeatureResult> DisableWindowsImageFeatureAsync(
            CancellationToken token,
            uint featureId = WindowsImageFeature.DefaultFeatureId
        ) => Set(false, featureId);

        private async Task<WindowsImageFeatureResult> Set(bool enabled, uint featureId)
        {
            Directions.Add(enabled);
            FeatureIds.Add(featureId);
            if (Wait is not null)
            {
                await Wait;
            }
            return new WindowsImageFeatureResult
            {
                FeatureId = featureId,
                DesiredState = enabled ? "Enabled" : "Disabled",
                Outcome = enabled ? "Enabled" : "Disabled",
                ChangeAttempted = true,
                Changed = true,
                RuntimeChanged = true,
                NativeSetStatus = 0,
                After = State(enabled ? 2u : 1u, featureId),
            };
        }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }

        public Task<int> GrantAsync(string path, bool write, CancellationToken token) => throw new NotSupportedException();

        public Task StartTraceAsync(TraceScope scope, CancellationToken token) => throw new NotSupportedException();

        public Task<TraceBatch> ReadTraceAsync(CancellationToken token) => throw new NotSupportedException();

        public Task StopTraceAsync(CancellationToken token) => throw new NotSupportedException();
    }
}

namespace LockscreenGif.Contracts.Services
{
    // The explicit service depends only on these members; no WinUI surface is loaded by these tests.
    public interface ILockscreenService
    {
        bool IsApplying { get; }
        string CacheDirectory { get; }
    }
}
