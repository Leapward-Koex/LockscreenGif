using System.Diagnostics;

namespace Session.Tests;

internal static class Program
{
    private static int _checks;

    private static async Task Main()
    {
        var baseDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "LockscreenGif-SessionTests"));
        var directory = Path.GetFullPath(Path.Combine(baseDirectory, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        try
        {
            await LifecycleTests.RunAsync(directory);
            await CancellationTests.RunAsync(directory);
            await MemorySessionTests.RunAsync(directory);
            await ShutdownTests.RunAsync(directory);
            await TracingLifecycleTests.RunAsync(directory);
            await TraceRetentionTests.RunAsync();
            await TracePollingTests.RunAsync();
            await LockscreenVerificationTests.RunAsync();
            Console.WriteLine($"All {_checks} session lifecycle checks passed. No native lock-screen APIs were called.");
        }
        finally
        {
            if (!directory.StartsWith(baseDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Test cleanup path escaped its temporary root.");
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    internal static void Check(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidOperationException("FAILED: " + label);
        }

        _checks++;
        Console.WriteLine("PASS " + label);
    }

    internal static async Task WaitUntilAsync(Func<bool> condition, string label)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(15))
            {
                throw new TimeoutException(label);
            }

            await Task.Delay(40);
        }
    }
}
