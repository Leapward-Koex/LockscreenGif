using LockscreenGif.Models;

namespace LockscreenGif.Services.Lockscreen;

internal sealed class ApplyProgress(Action<LockscreenApplyEvent>? callback)
{
    public void Report(string stage, string message, string? path = null, string severity = "Info")
    {
        Logger.Info($"Lockscreen apply Stage={stage} Severity={severity} Path=\"{path}\" {message}");
        try
        {
            callback?.Invoke(
                new LockscreenApplyEvent
                {
                    Stage = stage,
                    Message = message,
                    Path = path,
                    Severity = severity,
                }
            );
        }
        catch (Exception ex)
        {
            // A diagnostic collector failure must not change the operation it observes.
            Logger.Error("Lockscreen apply progress collector failed", ex);
        }
    }

    public static string Describe(Exception ex) => $"{ex.GetType().Name} (HRESULT 0x{ex.HResult:X8}): {ex.Message}";
}
