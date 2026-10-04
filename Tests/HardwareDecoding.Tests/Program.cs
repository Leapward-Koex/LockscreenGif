using System.Diagnostics;
using LockscreenGif.Services;

await CapabilityTests.RunAsync();
VideoEditingPreferencesTests.Run();
VideoEditingSettingsStateTests.Run();
Console.WriteLine("Hardware decoding capability and preference regression tests passed.");

if (args.Contains("--probe", StringComparer.Ordinal))
{
    using var capability = new HardwareDecodingCapabilityService();
    var timer = Stopwatch.StartNew();
    await capability.EnsureCheckedAsync();
    Console.WriteLine($"Native discovery: {capability.Snapshot.Availability}, {timer.Elapsed.TotalMilliseconds:F0} ms");
    if (capability.Snapshot.Availability == HardwareDecodingAvailability.CheckFailed)
    {
        throw new InvalidOperationException("Native hardware discovery failed.");
    }
}
