using LockscreenGif.Services.Diagnostics;

internal static class SessionMonitorTests
{
    internal static void Run(Action<bool, string> check)
    {
        using var monitor = new WindowsSessionMonitor();
        var observations = new List<string>();
        var activations = 0;
        monitor.Observed += observations.Add;
        monitor.WindowActivated += () => activations++;

        monitor.ObserveMessage(0x001C, 1, IntPtr.Zero);
        check(
            activations == 1 && observations.Count == 0,
            "Native app activation reaches prerequisite listeners without triggering diagnostic observations"
        );
        monitor.ObserveMessage(0x001C, 0, IntPtr.Zero);
        check(
            activations == 1 && observations.Count == 0,
            "Native app deactivation triggers neither prerequisite refresh nor diagnostic observations"
        );

        var currentSession = new IntPtr(monitor.SessionId);
        var otherSession = new IntPtr(monitor.SessionId + 1);
        monitor.ObserveMessage(0x02B1, 8, currentSession);
        check(
            activations == 1 && observations.SequenceEqual(new[] { "SessionUnlock" }),
            "The current session's unlock remains a diagnostic observation without becoming app activation"
        );
        monitor.ObserveMessage(0x02B1, 8, otherSession);
        check(
            activations == 1 && observations.SequenceEqual(new[] { "SessionUnlock" }),
            "An unlock from another Windows session is ignored"
        );
        monitor.ObserveMessage(0x02B1, 7, currentSession);
        check(
            activations == 1 && observations.SequenceEqual(new[] { "SessionUnlock", "SessionLock" }),
            "The current session's lock retains its diagnostic observation"
        );
        monitor.ObserveMessage(0x02B1, 7, otherSession);
        check(
            activations == 1 && observations.SequenceEqual(new[] { "SessionUnlock", "SessionLock" }),
            "A lock from another Windows session is ignored"
        );
        check(
            !monitor.IsRegistered && !monitor.PowerNotificationsAvailable,
            "Session message tests use production routing without starting native monitoring"
        );
    }
}
