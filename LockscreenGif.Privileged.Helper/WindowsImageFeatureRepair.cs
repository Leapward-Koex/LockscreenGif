using System.Runtime.InteropServices;
using LockscreenGif.Privileged;
using Microsoft.Win32;

namespace LockscreenGif.Privileged.Helper;

/// <summary>Changes the selected feature at user priority with fixed state-only operations.</summary>
internal static class WindowsImageFeatureRepair
{
    internal static WindowsImageFeatureResult EnsureDisabled(uint featureId = WindowsImageFeature.DefaultFeatureId) =>
        EnsureState(false, () => WindowsImageFeature.Read(featureId), SetRuntime, WriteOverride, featureId);

    internal static WindowsImageFeatureResult EnsureEnabled(uint featureId = WindowsImageFeature.DefaultFeatureId) =>
        EnsureState(true, () => WindowsImageFeature.Read(featureId), SetRuntime, WriteOverride, featureId);

    internal static WindowsImageFeatureResult EnsureState(
        bool enabled,
        Func<WindowsImageFeatureState> read,
        Func<uint, uint, int> setRuntime,
        Action<WindowsImageFeatureState, uint> persist,
        uint featureId = WindowsImageFeature.DefaultFeatureId
    )
    {
        ArgumentOutOfRangeException.ThrowIfZero(featureId);
        var desired = enabled ? 2u : 1u;
        var before = read();
        if (before.FeatureId != featureId)
        {
            return new WindowsImageFeatureResult
            {
                FeatureId = featureId,
                DesiredState = enabled ? "Enabled" : "Disabled",
                Outcome = "Failed",
                Error = "The feature readback does not match the selected identifier.",
            };
        }
        var result = WindowsImageFeature.Evaluate(before, enabled);
        if (result.Outcome != "NeedsChange")
        {
            return result;
        }

        var runtimeAttempted = false;
        var persistenceAttempted = false;
        result.ChangeAttempted = true;
        try
        {
            if (before.RuntimeState != desired)
            {
                runtimeAttempted = true;
                result.NativeSetStatus = setRuntime(featureId, desired);
                if (result.NativeSetStatus != 0)
                {
                    result.Error =
                        $"The Windows runtime feature update failed with NTSTATUS 0x{unchecked((uint)result.NativeSetStatus.Value):X8}.";
                }
            }
            if (result.Error is null && (before.OverrideState != desired || before.OverrideOptions != 0))
            {
                persistenceAttempted = true;
                persist(before, desired);
            }
        }
        catch (Exception ex)
        {
            result.Error = $"{ex.GetType().Name} (0x{unchecked((uint)ex.HResult):X8}): {ex.Message}";
        }

        try
        {
            result.After = read();
            if (result.After.FeatureId != featureId)
            {
                throw new InvalidDataException("The feature verification does not match the selected identifier.");
            }
        }
        catch (Exception ex)
        {
            result.After = null;
            result.Outcome = "Failed";
            result.ChangeOutcomeUnknown = runtimeAttempted || persistenceAttempted;
            result.Error =
                (result.Error is null ? "" : result.Error + " ")
                + $"Verification failed: {ex.GetType().Name} (0x{unchecked((uint)ex.HResult):X8}): {ex.Message}";
            return result;
        }
        var runtimeReadable = result.After.QueryStatus == 0 && result.After.QueryError is null && result.After.RuntimeState is 1 or 2;
        var persistenceReadable = result.After.OverrideError is null && result.After.OverrideExists is not null;
        result.ChangeOutcomeUnknown = (runtimeAttempted && !runtimeReadable) || (persistenceAttempted && !persistenceReadable);
        result.RuntimeChanged = runtimeReadable && result.After.RuntimeState != before.RuntimeState;
        var persistenceChanged = !persistenceReadable
            ? (result.After.OverrideExists is bool exists && exists != before.OverrideExists)
                || (result.After.OverrideState is int state && state != before.OverrideState)
                || (result.After.OverrideOptions is int options && options != before.OverrideOptions)
            : result.After.OverrideExists != before.OverrideExists
                || result.After.OverrideState != before.OverrideState
                || result.After.OverrideOptions != before.OverrideOptions;
        result.Changed = result.RuntimeChanged || persistenceChanged;

        if (
            !runtimeReadable
            || result.After.RuntimeState != desired
            || !persistenceReadable
            || result.After.OverrideState != desired
            || result.After.OverrideOptions != 0
        )
        {
            result.Outcome = "Failed";
            result.Error ??=
                result.After.QueryError
                ?? result.After.OverrideError
                ?? "The requested runtime state and persistent override could not both be verified.";
            return result;
        }

        if (result.Error is not null)
        {
            result.Outcome = "Failed";
            return result;
        }

        result.Outcome = result.DesiredState;
        return result;
    }

    private static int SetRuntime(uint featureId, uint desired)
    {
        ulong stamp = 0;
        return RtlSetFeatureConfigurations(ref stamp, 1, [CreateRuntimeUpdate(desired, featureId)], 1);
    }

    internal static FeatureUpdate CreateRuntimeUpdate(uint desired, uint featureId = WindowsImageFeature.DefaultFeatureId)
    {
        ArgumentOutOfRangeException.ThrowIfZero(featureId);
        if (desired is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(desired), "Only the fixed Enabled or Disabled state is supported.");
        }
        return new FeatureUpdate
        {
            FeatureId = featureId,
            Priority = 8,
            EnabledState = desired,
            Operation = 1,
        };
    }

    // ViVe's ntdll setter ABI uses eight consecutive 32-bit fields. Operation1 changes state only, preserving variants.
    [StructLayout(LayoutKind.Sequential)]
    internal struct FeatureUpdate
    {
        internal uint FeatureId;
        internal uint Priority;
        internal uint EnabledState;
        internal uint EnabledStateOptions;
        internal uint Variant;
        internal uint VariantPayloadKind;
        internal uint VariantPayload;
        internal uint Operation;
    }

    [DllImport("ntdll.dll", ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RtlSetFeatureConfigurations(
        ref ulong previousChangeStamp,
        uint configurationType,
        [In] FeatureUpdate[] updates,
        int count
    );

    private static void WriteOverride(WindowsImageFeatureState expected, uint desired)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        var overridePath = WindowsImageFeature.GetOverridePath(expected.FeatureId);
        using var existing = machine.OpenSubKey(overridePath, writable: true);
        if ((existing is not null) != expected.OverrideExists)
        {
            throw new IOException(
                "The pending feature override changed after it was checked. Retry the feature action to read the new configuration."
            );
        }

        using var key = existing is null ? machine.CreateSubKey(overridePath, writable: true) : null;
        var target = existing ?? key ?? throw new IOException("The feature override could not be opened for writing.");
        WriteValues(expected, new RegistryOverrideValues(target), desired);
    }

    internal static void WriteValues(WindowsImageFeatureState expected, IOverrideValues target, uint desired = 1)
    {
        ArgumentOutOfRangeException.ThrowIfZero(expected.FeatureId);
        if (desired is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(desired), "Only the fixed Enabled or Disabled state is supported.");
        }

        if (
            ReadDword(target, "EnabledState", 2) != expected.OverrideState
            || ReadDword(target, "EnabledStateOptions", 1) != expected.OverrideOptions
        )
        {
            throw new IOException(
                "The pending feature override changed while it was being opened. Retry the feature action to read the new configuration."
            );
        }

        // Preserve every unrelated value and subkey, including Windows-maintained metadata such as TelemetryFlags.
        target.SetDword("EnabledState", (int)desired);
        target.SetDword("EnabledStateOptions", 0);
        target.Flush();
    }

    private static int? ReadDword(IOverrideValues key, string name, int maximum)
    {
        var value = key.Read(name);
        if (value is null)
        {
            return null;
        }

        if (key.Kind(name) != RegistryValueKind.DWord || value is not int number || number < 0 || number > maximum)
        {
            throw new InvalidDataException($"The feature override value {name} has an unexpected type or value.");
        }

        return number;
    }

    internal interface IOverrideValues
    {
        object? Read(string name);
        RegistryValueKind Kind(string name);
        void SetDword(string name, int value);
        void Flush();
    }

    private sealed class RegistryOverrideValues(RegistryKey key) : IOverrideValues
    {
        public object? Read(string name) => key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);

        public RegistryValueKind Kind(string name) => key.GetValueKind(name);

        public void SetDword(string name, int value) => key.SetValue(name, value, RegistryValueKind.DWord);

        public void Flush() => key.Flush();
    }
}
