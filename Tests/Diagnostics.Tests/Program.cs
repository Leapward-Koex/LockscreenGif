namespace Diagnostics.Tests;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--analyze-report")
        {
            foreach (var path in args.Skip(1))
            {
                ReportReplay.Analyze(path);
            }
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "LockscreenGif-diagnostics-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await GifTests.RunAsync(directory);
            await ReportTests.RunAsync(directory);
            PrivacyTests.Run();
            await ComparisonReportTests.RunAsync(directory);
            await RecorderTests.RunAsync(directory);
            RetentionTests.Run();
            AnalyzerTests.Run();
            CacheFindingTimingTests.Run();
            ActivityFindingTests.Run();
            FindingGroupTests.Run();
            SuccessfulTraceTests.Run();
            ReadTimingTests.Run();
            await TraceReportTests.RunAsync(directory);
            await ShutdownReportTests.RunAsync(directory);
            await CacheTests.RunAsync(directory);
            await LogArchiveTests.RunAsync(directory);
            await DiagnosticLogAttachmentTests.RunAsync(directory);
            Console.WriteLine("All diagnostics checks passed.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static void Check(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidOperationException("FAILED: " + label);
        }

        Console.WriteLine("PASS " + label);
    }
}
