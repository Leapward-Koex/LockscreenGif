using LockscreenGif.Privileged;
using LockscreenGif.Privileged.Helper.Tracing;

namespace ProcessTracing.Tests;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--fixture")
        {
            NativeCollectorTests.Fixture(args[1]);
            return 0;
        }
        if (args.Length > 0 && args[0] == "--transport")
        {
            try
            {
                await NativeTransportTests.RunAsync(args[1]);
                return 0;
            }
            catch (Exception ex)
            {
                await File.WriteAllTextAsync(args[1], ex.ToString());
                return 1;
            }
        }
        if (args.Length > 0 && args[0] == "--native")
        {
            try
            {
                await NativeCollectorTests.RunAsync(args[1]);
                return 0;
            }
            catch (Exception ex)
            {
                await File.WriteAllTextAsync(args[1], ex.ToString());
                return 1;
            }
        }
        CorrelationTests.Run();
        ReuseTests.Run();
        TraceTimingTests.Run();
        ActivityTimingTests.Run();
        await ShutdownProgressTests.RunAsync();
        await ProtocolTests.RunAsync();
        await PermissionScopeTests.RunAsync();
        Console.WriteLine("All isolated tracing checks passed. No elevation or native tracing was requested.");
        return 0;
    }

    internal static void Check(bool value, string label)
    {
        if (!value)
        {
            throw new InvalidOperationException("FAILED: " + label);
        }

        Console.WriteLine("PASS " + label);
    }
}
