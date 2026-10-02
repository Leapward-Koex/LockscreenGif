namespace LockscreenGif.Privileged;

/// <summary>Caller-observed runtime configuration and the separately persisted next-boot override.</summary>
public sealed class WindowsImageFeatureState
{
    public uint FeatureId { get; set; } = WindowsImageFeature.DefaultFeatureId;
    public DateTimeOffset? ObservedAt { get; set; }
    public int? QueryStatus { get; set; }
    public uint? RuntimeState { get; set; }
    public uint? RuntimePriority { get; set; }
    public string? QueryError { get; set; }
    public bool? OverrideExists { get; set; }
    public int? OverrideState { get; set; }
    public int? OverrideOptions { get; set; }
    public string? OverrideError { get; set; }
}

/// <summary>The selected feature request may have changed Windows, but its result could not be confirmed.</summary>
public sealed class WindowsImageFeatureDispatchException(string message, Exception? innerException = null)
    : IOException(message, innerException);

/// <summary>Configuration evidence only; neither a successful override nor a query proves visible animation.</summary>
public sealed class WindowsImageFeatureResult
{
    public uint FeatureId { get; set; } = WindowsImageFeature.DefaultFeatureId;
    public string DesiredState { get; set; } = "Disabled";
    public string Outcome { get; set; } = "NotChecked";
    public WindowsImageFeatureState? Before { get; set; }
    public WindowsImageFeatureState? After { get; set; }
    public bool ChangeAttempted { get; set; }
    public bool Changed { get; set; }
    public bool RuntimeChanged { get; set; }
    public int? NativeSetStatus { get; set; }
    public bool ChangeOutcomeUnknown { get; set; }
    public string? Error { get; set; }
}
