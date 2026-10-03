using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace LockscreenGif.Privileged;

/// <summary>Reads the current image-pipeline feature and its pending boot override independently.</summary>
public static class WindowsImageFeature
{
    public const uint DefaultFeatureId = 38943831;

    public static string GetOverridePath(uint featureId)
    {
        ArgumentOutOfRangeException.ThrowIfZero(featureId);
        // Windows encodes the numeric ID in its registry leaf. Express the observed transform with standard bit operations.
        var reversed = BinaryPrimitives.ReverseEndianness(featureId ^ 0x74161A4Eu);
        var encoded = BitOperations.RotateLeft(reversed ^ 0x8FB23D4Fu, 1) ^ 0x833EA8FFu;
        return @"SYSTEM\CurrentControlSet\Control\FeatureManagement\Overrides\8\" + encoded.ToString(CultureInfo.InvariantCulture);
    }

    public static WindowsImageFeatureState Read() => Read(DefaultFeatureId);

    public static WindowsImageFeatureState Read(uint featureId)
    {
        ArgumentOutOfRangeException.ThrowIfZero(featureId);
        var state = new WindowsImageFeatureState { FeatureId = featureId, ObservedAt = DateTimeOffset.UtcNow };
        if (!OperatingSystem.IsWindows())
        {
            state.QueryError = "Windows feature configuration is unavailable on this operating system.";
            return state;
        }

        try
        {
            ulong stamp = 0;
            state.QueryStatus = RtlQueryFeatureConfiguration(featureId, 1, ref stamp, out var configuration);
            if (state.QueryStatus == 0)
            {
                if (configuration.FeatureId != featureId)
                {
                    state.QueryError = "Windows returned a different feature identifier.";
                }
                else
                {
                    state.RuntimeState = (configuration.Flags >> 4) & 3;
                    state.RuntimePriority = configuration.Flags & 15;
                }
            }
        }
        catch (Exception ex)
        {
            state.QueryError = $"{ex.GetType().Name} (0x{unchecked((uint)ex.HResult):X8}): {ex.Message}";
        }

        ReadOverride(state);
        return state;
    }

    /// <summary>Classifies a snapshot without assuming that an absent or default feature is enabled.</summary>
    public static WindowsImageFeatureResult Evaluate(WindowsImageFeatureState state, bool enabled = false)
    {
        var desired = enabled ? 2u : 1u;
        var desiredName = enabled ? "Enabled" : "Disabled";
        var result = new WindowsImageFeatureResult
        {
            FeatureId = state.FeatureId,
            DesiredState = desiredName,
            Before = state,
            After = state,
        };
        if (state.FeatureId == 0)
        {
            result.Outcome = "Failed";
            result.Error = "The Windows feature identifier must be a positive 32-bit integer.";
            return result;
        }
        if (state.QueryStatus != 0 || state.QueryError is not null || state.RuntimeState is not (1 or 2))
        {
            result.Outcome = "Unavailable";
            result.Error = state.QueryError ?? "Windows did not return an explicit enabled or disabled runtime configuration.";
            return result;
        }

        if (state.OverrideError is not null || state.OverrideExists is null)
        {
            result.Outcome = "Failed";
            result.Error = state.OverrideError ?? "The pending boot override could not be read.";
            return result;
        }

        if (
            state.OverrideState is < 0 or > 2
            || state.OverrideOptions is < 0 or > 1
            || (state.OverrideExists == false && (state.OverrideState is not null || state.OverrideOptions is not null))
        )
        {
            result.Outcome = "Failed";
            result.Error = "The pending boot override contains an unexpected type or value.";
            return result;
        }

        if (state.RuntimeState != desired && state.RuntimePriority is > 8)
        {
            result.Outcome = "Failed";
            result.Error =
                "A higher-priority Windows configuration conflicts with the requested state; the user-priority override cannot replace it.";
            return result;
        }

        if (state.RuntimeState == desired && state.OverrideState != (enabled ? 1 : 2))
        {
            result.Outcome = $"Already{desiredName}";
            return result;
        }

        result.Outcome = "NeedsChange";
        return result;
    }

    public static string Describe(WindowsImageFeatureState state) =>
        $"Feature={state.FeatureId}; ObservedAt={state.ObservedAt?.ToString("O") ?? "Unknown"}; RuntimeState={StateName(state.RuntimeState)}; RuntimePriority={state.RuntimePriority?.ToString() ?? "Unknown"}; "
        + $"QueryStatus={(state.QueryStatus is int status ? $"0x{unchecked((uint)status):X8}" : "Unavailable")}; "
        + $"NextBootOverrideExists={state.OverrideExists?.ToString() ?? "Unknown"}; NextBootOverrideState={StateName(state.OverrideState is int value ? unchecked((uint)value) : null)}; "
        + $"NextBootOverrideOptions={state.OverrideOptions?.ToString() ?? "Absent"}; QueryError={state.QueryError ?? "None"}; OverrideError={state.OverrideError ?? "None"}";

    private static string StateName(uint? state) =>
        state switch
        {
            0 => "Default(0)",
            1 => "Disabled(1)",
            2 => "Enabled(2)",
            null => "UnknownOrAbsent",
            _ => $"Invalid({state})",
        };

    [SupportedOSPlatform("windows")]
    private static void ReadOverride(WindowsImageFeatureState state)
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(GetOverridePath(state.FeatureId));
            state.OverrideExists = key is not null;
            if (key is null)
            {
                return;
            }

            state.OverrideState = ReadDword(key, "EnabledState", 2);
            state.OverrideOptions = ReadDword(key, "EnabledStateOptions", 1);
        }
        catch (Exception ex)
        {
            state.OverrideError = $"{ex.GetType().Name} (0x{unchecked((uint)ex.HResult):X8}): {ex.Message}";
        }
    }

    [SupportedOSPlatform("windows")]
    private static int? ReadDword(RegistryKey key, string name, int maximum)
    {
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null)
        {
            return null;
        }

        if (key.GetValueKind(name) != RegistryValueKind.DWord || value is not int number || number < 0 || number > maximum)
        {
            throw new InvalidDataException($"The feature override value {name} has an unexpected type or value.");
        }

        return number;
    }

    // The named ntdll query export is undocumented. ViVe and the measured Windows caller agree on this 12-byte ABI.
    // Boot=0 queries the loaded boot store; Runtime=1 queries the current runtime store. Neither reads pending registry changes.
    [StructLayout(LayoutKind.Sequential)]
    internal struct FeatureConfiguration
    {
        internal uint FeatureId;
        internal uint Flags;
        internal uint VariantPayload;
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RtlQueryFeatureConfiguration(
        uint featureId,
        uint configurationType,
        ref ulong changeStamp,
        out FeatureConfiguration configuration
    );
}
