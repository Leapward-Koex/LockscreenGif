using LockscreenGif.Contracts.Services;
using LockscreenGif.Privileged;
using LockscreenGif.Services.Diagnostics;

namespace LockscreenGif.Services.Lockscreen;

/// <summary>Performs only explicit user-requested changes to the selected Windows image feature.</summary>
public sealed class WindowsImageFeatureService
{
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Func<IPrivilegedOperationSession> _createSession;
    private readonly Func<bool> _isApplying;
    private readonly Func<uint, WindowsImageFeatureState> _read;
    private readonly Func<uint> _getFeatureId;

    public WindowsImageFeatureService(PrivilegedSessionFactory factory, ILockscreenService lockscreen)
        : this(
            () => factory.Create(lockscreen.CacheDirectory),
            () => lockscreen.IsApplying,
            WindowsImageFeature.Read,
            () => WindowsImageFeatureSettings.Current.FeatureId
        ) { }

    internal WindowsImageFeatureService(
        Func<IPrivilegedOperationSession> createSession,
        Func<bool> isApplying,
        Func<uint, WindowsImageFeatureState> read,
        Func<uint>? getFeatureId = null
    )
    {
        _createSession = createSession;
        _isApplying = isApplying;
        _read = read;
        _getFeatureId = getFeatureId ?? (() => WindowsImageFeature.DefaultFeatureId);
    }

    public bool IsBusy => _serial.CurrentCount == 0;

    public async Task WaitForIdleAsync()
    {
        await _serial.WaitAsync();
        _serial.Release();
    }

    public async Task<WindowsImageFeatureResult> SetEnabledAsync(bool enabled, CancellationToken token = default)
    {
        var featureId = _getFeatureId();
        var result = new WindowsImageFeatureResult { FeatureId = featureId, DesiredState = enabled ? "Enabled" : "Disabled" };
        var acquired = false;
        var replyReceived = false;
        try
        {
            await _serial.WaitAsync(token);
            acquired = true;
            if (featureId == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(featureId), "The Windows feature ID must be greater than zero.");
            }
            if (_isApplying())
            {
                result.Outcome = "Failed";
                result.Error = "Wait for the current lock-screen operation to finish before changing the Windows feature.";
            }
            else
            {
                token.ThrowIfCancellationRequested();
                var before = _read(featureId);
                if (before.FeatureId != featureId)
                {
                    throw new InvalidDataException("The feature observation does not match the selected ID.");
                }
                result = WindowsImageFeature.Evaluate(before, enabled);
                Logger.Info(
                    $"Explicit Windows image feature action desired={result.DesiredState}; Before: {WindowsImageFeature.Describe(before)}"
                );
                if (result.Outcome == "NeedsChange")
                {
                    token.ThrowIfCancellationRequested();
                    await using var session = _createSession();
                    result = enabled
                        ? await session.EnableWindowsImageFeatureAsync(token, featureId)
                        : await session.DisableWindowsImageFeatureAsync(token, featureId);
                    replyReceived = true;
                    result.Before ??= before;
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            result.Outcome = "Cancelled";
            result.ChangeAttempted = false;
            result.Error = Describe(ex);
        }
        catch (Exception ex)
        {
            result.Outcome = "Failed";
            if (!replyReceived && ex is WindowsImageFeatureDispatchException)
            {
                result.ChangeAttempted = true;
                result.ChangeOutcomeUnknown = true;
                result.After = null;
            }
            result.Error =
                Describe(ex)
                + (
                    result.ChangeOutcomeUnknown
                        ? " The feature change outcome cannot be confirmed because the helper result was not received."
                        : ""
                );
        }
        finally
        {
            if (acquired)
            {
                _serial.Release();
            }
        }

        Logger.Info(
            $"Explicit Windows image feature action: Feature={result.FeatureId}; desired={result.DesiredState}; outcome={result.Outcome}; "
                + $"changeAttempted={result.ChangeAttempted}; changed={result.Changed}; runtimeChanged={result.RuntimeChanged}; changeOutcomeUnknown={result.ChangeOutcomeUnknown}; "
                + $"nativeSetStatus={(result.NativeSetStatus is int status ? $"0x{unchecked((uint)status):X8}" : "NotCalled")}; "
                + (result.After is null ? "After=Unavailable; " : $"After: {WindowsImageFeature.Describe(result.After)}; ")
                + $"Error={result.Error ?? "None"}"
        );
        return result;
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name} (HRESULT 0x{ex.HResult:X8}): {ex.Message}";
}
