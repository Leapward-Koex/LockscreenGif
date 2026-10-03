using System.Runtime.InteropServices;
using System.Text.Json;
using LockscreenGif.Privileged;
using LockscreenGif.Privileged.Helper;
using Microsoft.Win32;

if (args is ["--read-current"])
{
    Console.WriteLine(JsonSerializer.Serialize(WindowsImageFeature.Read(), new JsonSerializerOptions { WriteIndented = true }));
    return;
}

var tests = new (string Name, Action Run)[]
{
    ("The native query and state-only update use their fixed ABIs", NativeAbi),
    ("Missing and default configurations never write", UnknownConfigurations),
    ("Already disabled configurations need no write", AlreadyDisabled),
    ("A persisted target with a differing runtime updates runtime only", PendingRuntime),
    ("An enabled feature persists only the two measured DWORD values", PersistDisabled),
    ("A conflicting next-boot enable is repaired even when runtime is disabled", ConflictingBootEnable),
    ("Higher-priority enabled configurations cannot be repaired at user priority", HigherPriority),
    ("Malformed override snapshots fail closed", InvalidSnapshot),
    ("Unexpected registry value types and concurrent changes are preserved", InvalidOrChangedRegistry),
    ("Explicit enable uses state2 for runtime and persistence", EnableFeature),
    ("Native update failures prevent persistence and retain NTSTATUS", NativeFailure),
    ("A successful native call without matching readback is a failure", RuntimeVerification),
    ("Partial writes retain runtime changes, failure and HRESULT", PartialWrite),
    ("Unreadable verification does not claim an observed change", UnreadableVerification),
    ("A verification exception retains uncertainty after runtime dispatch", VerificationException),
    ("Descriptions distinguish current runtime from next-boot persistence", Description),
    ("Selected IDs derive the correct registry leaf across the uint range", SelectedIdEncoding),
    ("A custom ID stays consistent through runtime, persistence and evidence", SelectedIdScope),
    ("Zero is rejected before reading or changing feature configuration", InvalidSelectedId),
    ("Mismatched feature readbacks cannot be treated as selected-feature evidence", MismatchedSelectedId),
};

foreach (var test in tests)
{
    test.Run();
    Console.WriteLine($"PASS {test.Name}");
}
Console.WriteLine($"{tests.Length} Windows image feature tests passed. No host feature settings were changed.");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void NativeAbi()
{
    Assert(Marshal.SizeOf<WindowsImageFeature.FeatureConfiguration>() == 12, "The native record must be exactly 12 bytes.");
    Assert(Marshal.OffsetOf<WindowsImageFeature.FeatureConfiguration>("Flags").ToInt32() == 4, "State flags offset changed.");
    Assert(Marshal.OffsetOf<WindowsImageFeature.FeatureConfiguration>("VariantPayload").ToInt32() == 8, "Payload offset changed.");
    Assert(Marshal.SizeOf<WindowsImageFeatureRepair.FeatureUpdate>() == 32, "The native update must be exactly eight DWORDs.");
    Assert(Marshal.OffsetOf<WindowsImageFeatureRepair.FeatureUpdate>("Operation").ToInt32() == 28, "Native operation offset changed.");
    foreach (var desired in new uint[] { 1, 2 })
    {
        var update = WindowsImageFeatureRepair.CreateRuntimeUpdate(desired);
        Assert(
            update.FeatureId == 38943831 && update.Priority == 8 && update.EnabledState == desired && update.Operation == 1,
            "Native update scope must remain fixed and state-only."
        );
        Assert(
            update.Variant == 0 && update.VariantPayloadKind == 0 && update.VariantPayload == 0 && update.EnabledStateOptions == 0,
            "Variant fields must not be supplied."
        );
    }
    Assert(WindowsImageFeature.DefaultFeatureId == 38943831, "The measured feature remains the default.");
    Assert(
        WindowsImageFeature.GetOverridePath(WindowsImageFeature.DefaultFeatureId)
            == @"SYSTEM\CurrentControlSet\Control\FeatureManagement\Overrides\8\2920700556",
        "The repair must use the exact measured priority and encoded leaf."
    );
}

static WindowsImageFeatureState State(uint runtime = 2, int? pending = null, int? options = null) =>
    new()
    {
        QueryStatus = 0,
        RuntimeState = runtime,
        RuntimePriority = 0,
        OverrideExists = pending is not null || options is not null,
        OverrideState = pending,
        OverrideOptions = options,
    };

static WindowsImageFeatureResult NoWrite(WindowsImageFeatureState state) =>
    WindowsImageFeatureRepair.EnsureState(
        false,
        () => state,
        (_, _) => throw new InvalidOperationException("This state must not set runtime."),
        (_, _) => throw new InvalidOperationException("This state must not persist.")
    );

static void UnknownConfigurations()
{
    foreach (var runtime in new uint[] { 0, 3 })
    {
        Assert(NoWrite(State(runtime)).Outcome == "Unavailable", "Default and invalid native states are unknown.");
    }
    var absent = State();
    absent.QueryStatus = unchecked((int)0xC0000225);
    Assert(NoWrite(absent).Outcome == "Unavailable", "A failed query is not an enabled feature.");
    var missingExport = State();
    missingExport.QueryError = "EntryPointNotFoundException";
    Assert(NoWrite(missingExport).Outcome == "Unavailable", "A missing API cannot be repaired.");
}

static void AlreadyDisabled()
{
    var result = NoWrite(State(1));
    Assert(result.Outcome == "AlreadyDisabled" && !result.ChangeAttempted, "Already disabled should be a no-op.");
}

static void PendingRuntime()
{
    var snapshots = new Queue<WindowsImageFeatureState>([State(2, 1, 0), State(1, 1, 0)]);
    var nativeCalls = 0;
    var result = WindowsImageFeatureRepair.EnsureState(
        false,
        snapshots.Dequeue,
        (_, desired) =>
        {
            Assert(desired == 1, "Expected disable.");
            nativeCalls++;
            return 0;
        },
        (_, _) => throw new Exception("Persistence is already correct.")
    );
    Assert(
        result.Outcome == "Disabled" && result.RuntimeChanged && nativeCalls == 1,
        "The explicit action must update and verify current runtime."
    );
}

static void PersistDisabled()
{
    var values = new FakeOverride();
    values.Values["TelemetryFlags"] = (1, RegistryValueKind.DWord);
    var snapshots = new Queue<WindowsImageFeatureState>([State(), State(1, 1, 0)]);
    var result = WindowsImageFeatureRepair.EnsureState(
        false,
        snapshots.Dequeue,
        (_, _) => 0,
        (before, desired) => WindowsImageFeatureRepair.WriteValues(before, values, desired)
    );
    Assert(
        result.Outcome == "Disabled" && result.Changed && result.RuntimeChanged && result.NativeSetStatus == 0,
        "Both current state and persistence must be verified."
    );
    Assert(values.Writes.SequenceEqual([("EnabledState", 1), ("EnabledStateOptions", 0)]), "Only the exact two DWORDs may change.");
    Assert((int)values.Values["TelemetryFlags"].Value == 1 && values.Values.Count == 3, "Unrelated metadata must survive.");
    Assert(values.Flushed, "Persistence must be flushed before verification.");
    Assert(!ReferenceEquals(result.Before, result.After), "Before and after must represent independent observations.");
}

static void ConflictingBootEnable()
{
    var snapshots = new Queue<WindowsImageFeatureState>([State(1, 2, 0), State(1, 1, 0)]);
    var writes = 0;
    var result = WindowsImageFeatureRepair.EnsureState(
        false,
        snapshots.Dequeue,
        (_, _) => throw new Exception("Runtime is already correct."),
        (_, _) => writes++
    );
    Assert(
        writes == 1 && result.Changed && result.Outcome == "Disabled" && result.NativeSetStatus is null,
        "The next boot must not re-enable the feature."
    );
}

static void HigherPriority()
{
    foreach (var priority in new uint[] { 9, 10, 12, 15 })
    {
        var state = State();
        state.RuntimePriority = priority;
        Assert(NoWrite(state).Outcome == "Failed", "A user override cannot supersede an enabled higher priority.");
    }
}

static void InvalidSnapshot()
{
    var unreadable = State();
    unreadable.OverrideError = "Access denied";
    Assert(NoWrite(unreadable).Outcome == "Failed", "Unreadable persistence is not an absent override.");
    Assert(NoWrite(State(2, 3, 0)).Outcome == "Failed", "Unknown state values must be preserved.");
    Assert(NoWrite(State(2, 1, 2)).Outcome == "Failed", "Unknown option values must be preserved.");
}

static void InvalidOrChangedRegistry()
{
    foreach (
        var value in new (object Value, RegistryValueKind Kind)[]
        {
            ("2", RegistryValueKind.String),
            (2L, RegistryValueKind.QWord),
            (3, RegistryValueKind.DWord),
        }
    )
    {
        var values = new FakeOverride();
        values.Values["EnabledState"] = value;
        AssertThrows(() => WindowsImageFeatureRepair.WriteValues(State(2, 2, 0), values));
        Assert(values.Writes.Count == 0, "Malformed values must never be rewritten.");
    }
    var changed = new FakeOverride();
    changed.Values["EnabledState"] = (1, RegistryValueKind.DWord);
    AssertThrows(() => WindowsImageFeatureRepair.WriteValues(State(), changed));
    Assert(changed.Writes.Count == 0, "A concurrent change must be preserved for a fresh apply.");
}

static void AssertThrows(Action action)
{
    try
    {
        action();
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException)
    {
        return;
    }
    throw new InvalidOperationException("Expected the unsafe write to be refused.");
}

static void PartialWrite()
{
    var snapshots = new Queue<WindowsImageFeatureState>([State(), State(1, 1)]);
    var values = new FakeOverride { FailOnName = "EnabledStateOptions" };
    var result = WindowsImageFeatureRepair.EnsureState(
        false,
        snapshots.Dequeue,
        (_, _) => 0,
        (before, desired) => WindowsImageFeatureRepair.WriteValues(before, values, desired)
    );
    Assert(
        result.Outcome == "Failed" && result.Changed && result.RuntimeChanged && result.NativeSetStatus == 0,
        "A partial persisted change must retain the already-completed runtime change."
    );
    Assert(result.Error!.Contains("0x80070005", StringComparison.Ordinal), "Access denial HRESULT must be retained.");
    Assert(values.Writes.SequenceEqual([("EnabledState", 1)]), "Only the completed first write should be recorded.");
}

static void UnreadableVerification()
{
    var unavailable = new WindowsImageFeatureState { OverrideError = "Access denied" };
    var snapshots = new Queue<WindowsImageFeatureState>([State(2, 2, 0), unavailable]);
    var result = WindowsImageFeatureRepair.EnsureState(false, snapshots.Dequeue, (_, _) => 0, (_, _) => { });
    Assert(result.Outcome == "Failed" && !result.Changed && result.ChangeOutcomeUnknown, "Failed readback cannot prove a changed setting.");
}

static void EnableFeature()
{
    var snapshots = new Queue<WindowsImageFeatureState>([State(1, 1, 0), State(2, 2, 0)]);
    var values = new FakeOverride();
    values.Values["EnabledState"] = (1, RegistryValueKind.DWord);
    values.Values["EnabledStateOptions"] = (0, RegistryValueKind.DWord);
    var result = WindowsImageFeatureRepair.EnsureState(
        true,
        snapshots.Dequeue,
        (_, desired) =>
        {
            Assert(desired == 2, "Enable must use2.");
            return 0;
        },
        (before, desired) => WindowsImageFeatureRepair.WriteValues(before, values, desired)
    );
    Assert(
        result.DesiredState == "Enabled" && result.Outcome == "Enabled" && result.RuntimeChanged,
        "Both state directions require verified evidence."
    );
    Assert(
        values.Writes.SequenceEqual([("EnabledState", 2), ("EnabledStateOptions", 0)]),
        "Enable must persist state2 only at the fixed override."
    );
    var higherPriority = State(1);
    higherPriority.RuntimePriority = 10;
    Assert(WindowsImageFeature.Evaluate(higherPriority, true).Outcome == "Failed", "Enable must also respect higher-priority policy.");
}

static void NativeFailure()
{
    var snapshots = new Queue<WindowsImageFeatureState>([State(), State()]);
    var result = WindowsImageFeatureRepair.EnsureState(
        false,
        snapshots.Dequeue,
        (_, _) => unchecked((int)0xC0000022),
        (_, _) => throw new Exception("Native failure must not persist.")
    );
    Assert(
        result.Outcome == "Failed" && !result.Changed && result.NativeSetStatus == unchecked((int)0xC0000022),
        "Native failure status must survive."
    );
    Assert(result.Error!.Contains("0xC0000022"), "Native failure message must include NTSTATUS.");
}

static void RuntimeVerification()
{
    var snapshots = new Queue<WindowsImageFeatureState>([State(), State(2, 1, 0)]);
    var result = WindowsImageFeatureRepair.EnsureState(false, snapshots.Dequeue, (_, _) => 0, (_, _) => { });
    Assert(
        result.Outcome == "Failed" && result.Changed && !result.RuntimeChanged,
        "Persistence alone cannot prove the requested runtime state."
    );
}

static void VerificationException()
{
    var reads = 0;
    var result = WindowsImageFeatureRepair.EnsureState(
        false,
        () => ++reads == 1 ? State() : throw new IOException("Synthetic read failure"),
        (_, _) => 0,
        (_, _) => { }
    );
    Assert(
        result.Outcome == "Failed" && result.ChangeOutcomeUnknown && result.After is null && result.NativeSetStatus == 0,
        "A failed readback after dispatch must retain native status and uncertainty."
    );
}

static void Description()
{
    var state = State(2, 1, 0);
    state.QueryStatus = unchecked((int)0xC0000022);
    var description = WindowsImageFeature.Describe(state);
    Assert(
        description.Contains("RuntimeState=Enabled(2)") && description.Contains("NextBootOverrideState=Disabled(1)"),
        "Loaded and pending states must be distinct."
    );
    Assert(description.Contains("0xC0000022"), "Native status must retain all unsigned hexadecimal digits.");
}

static void SelectedIdEncoding()
{
    const string prefix = @"SYSTEM\CurrentControlSet\Control\FeatureManagement\Overrides\8\";
    foreach (
        var (id, leaf) in new (uint, string)[]
        {
            (38943831, "2920700556"),
            (61653826, "2246682254"),
            (1, "40828552"),
            (uint.MaxValue, "4287693175"),
        }
    )
    {
        Assert(WindowsImageFeature.GetOverridePath(id) == prefix + leaf, "Selected feature encoding or fixed priority changed.");
    }
    var oldSnapshot = JsonSerializer.Deserialize<WindowsImageFeatureState>("{\"QueryStatus\":0,\"RuntimeState\":1}");
    Assert(oldSnapshot!.FeatureId == WindowsImageFeature.DefaultFeatureId, "Older reports retain the original default ID.");
}

static void SelectedIdScope()
{
    const uint selectedId = 61653826;
    foreach (var enabled in new[] { false, true })
    {
        var desired = enabled ? 2u : 1u;
        var before = State(3 - desired);
        before.FeatureId = selectedId;
        var after = State(desired, (int)desired, 0);
        after.FeatureId = selectedId;
        var snapshots = new Queue<WindowsImageFeatureState>([before, after]);
        var values = new FakeOverride();
        var result = WindowsImageFeatureRepair.EnsureState(
            enabled,
            snapshots.Dequeue,
            (id, state) =>
            {
                var update = WindowsImageFeatureRepair.CreateRuntimeUpdate(state, id);
                Assert(
                    update.FeatureId == selectedId && update.EnabledState == desired && update.Priority == 8 && update.Operation == 1,
                    "The native update must use only the captured selected feature and state."
                );
                return 0;
            },
            (expected, state) =>
            {
                Assert(
                    expected.FeatureId == selectedId && WindowsImageFeature.GetOverridePath(expected.FeatureId).EndsWith("2246682254"),
                    "Persistence must derive its path from the same selected ID."
                );
                WindowsImageFeatureRepair.WriteValues(expected, values, state);
            },
            selectedId
        );
        Assert(
            result.FeatureId == selectedId && result.Before?.FeatureId == selectedId && result.After?.FeatureId == selectedId,
            "Selected scope must survive every evidence record."
        );
        Assert(
            result.Outcome == (enabled ? "Enabled" : "Disabled") && WindowsImageFeature.Describe(after).Contains("Feature=61653826"),
            "Results and logs must name the selected feature."
        );
    }
}

static void InvalidSelectedId()
{
    foreach (
        var action in new Action[]
        {
            () => WindowsImageFeature.GetOverridePath(0),
            () => WindowsImageFeature.Read(0),
            () => WindowsImageFeatureRepair.CreateRuntimeUpdate(1, 0),
            () =>
                WindowsImageFeatureRepair.EnsureState(
                    false,
                    () => throw new Exception("Invalid ID must not read."),
                    (_, _) => throw new Exception("Invalid ID must not set runtime."),
                    (_, _) => throw new Exception("Invalid ID must not persist."),
                    0
                ),
        }
    )
    {
        try
        {
            action();
        }
        catch (ArgumentOutOfRangeException)
        {
            continue;
        }
        throw new InvalidOperationException("Zero must be rejected before any configuration access.");
    }
}

static void MismatchedSelectedId()
{
    const uint selectedId = 61653826;
    var beforeMismatch = WindowsImageFeatureRepair.EnsureState(
        false,
        () => State(),
        (_, _) => throw new Exception("Mismatched readback must not change runtime."),
        (_, _) => throw new Exception("Mismatched readback must not persist."),
        selectedId
    );
    Assert(
        beforeMismatch.FeatureId == selectedId && beforeMismatch.Outcome == "Failed" && !beforeMismatch.ChangeAttempted,
        "Before-snapshot mismatch must fail without changes."
    );
    var before = State();
    before.FeatureId = selectedId;
    var snapshots = new Queue<WindowsImageFeatureState>([before, State(1, 1, 0)]);
    var afterMismatch = WindowsImageFeatureRepair.EnsureState(false, snapshots.Dequeue, (_, _) => 0, (_, _) => { }, selectedId);
    Assert(
        afterMismatch.Outcome == "Failed" && afterMismatch.ChangeOutcomeUnknown && afterMismatch.After is null,
        "A different ID's state cannot prove the selected mutation result."
    );
}

internal sealed class FakeOverride : WindowsImageFeatureRepair.IOverrideValues
{
    internal Dictionary<string, (object Value, RegistryValueKind Kind)> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal List<(string Name, int Value)> Writes { get; } = [];
    internal string? FailOnName { get; init; }
    internal bool Flushed { get; private set; }

    public object? Read(string name) => Values.TryGetValue(name, out var value) ? value.Value : null;

    public RegistryValueKind Kind(string name) => Values[name].Kind;

    public void SetDword(string name, int value)
    {
        if (name == FailOnName)
        {
            throw new UnauthorizedAccessException("Synthetic access denial.");
        }
        Values[name] = (value, RegistryValueKind.DWord);
        Writes.Add((name, value));
    }

    public void Flush() => Flushed = true;
}
