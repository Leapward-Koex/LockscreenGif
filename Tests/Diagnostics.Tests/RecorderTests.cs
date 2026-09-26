using LockscreenGif.Models.Diagnostics;
using LockscreenGif.Services.Diagnostics;

namespace Diagnostics.Tests;

internal static class RecorderTests
{
    internal static Task RunAsync(string directory)
    {
        var before = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Order().ToArray();
        var recorder = new DiagnosticRecorder(new DiagnosticSession());
        var changed = 0;
        recorder.Changed += () => changed++;
        for (var i = 0; i < 2000; i++)
        {
            recorder.Add("File", "Observed change " + i);
        }

        var beforeOverflow = changed;
        recorder.Add("File", "Omitted change");
        var snapshot = recorder.Snapshot();
        Program.Check(
            snapshot.Events.Count == 2001 && snapshot.Events[^1].Severity == "Warning" && !snapshot.MonitoringComplete,
            "Event cap adds one loss marker and marks coverage incomplete"
        );
        Program.Check(changed > beforeOverflow, "First event loss notifies the UI immediately");
        for (var i = 0; i < 100; i++)
        {
            recorder.Add("File", "More omitted changes");
        }

        Program.Check(recorder.Snapshot().Events.Count == 2001, "Later events remain bounded");
        recorder.Update(s => s.Observation = "Animated correctly");
        Program.Check(recorder.Snapshot().Observation == "Animated correctly", "Observation is retained in memory");
        Program.Check(
            before.SequenceEqual(Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Order()),
            "Recording evidence does not create files"
        );
        return Task.CompletedTask;
    }
}
