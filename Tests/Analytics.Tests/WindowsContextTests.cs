using System.Diagnostics;
using System.Runtime.InteropServices;
using LockscreenGif.Services.Analytics;

internal static class WindowsContextTests
{
    public static async Task RunAsync()
    {
        await PayloadAsync();
        Console.WriteLine("PASS Windows edition, locale, language, build and execution context enrich only app_opened");
        await InvalidValuesAsync();
        Console.WriteLine("PASS Windows context drops custom text and invalid values while preserving unknown numeric SKUs");
        await UnavailableAsync();
        Console.WriteLine("PASS Windows context failures preserve the launch event and disabled analytics never probes Windows");
        await BlockedCollectorAsync();
        Console.WriteLine("PASS slow local context collection cannot block capture, opt-out or stop, or bypass opt-out");
        if (OperatingSystem.IsWindows())
        {
            var native = WindowsAnalyticsContextCollector.Collect();
            Check(native.ProductSku is > 0 && native.Build is > 0, "Native Windows edition and build reads succeed.");
            Check(!string.IsNullOrEmpty(native.UserLocale) && !string.IsNullOrEmpty(native.SystemLocale), "Native locale signatures work.");
            Check(
                !string.IsNullOrEmpty(native.DisplayLanguage) && !string.IsNullOrEmpty(native.InstallLanguage),
                "Native language signatures work."
            );
            Console.WriteLine("PASS native Windows context read smoke check (local only; values are not logged or sent)");
        }
    }

    private static async Task PayloadAsync()
    {
        using var context = new TestContext(blockFirst: true);
        var calls = 0;
        using var service = context.Create(windowsContext: () =>
        {
            Interlocked.Increment(ref calls);
            return new AnalyticsWindowsContext
            {
                ProductSku = 0x31,
                Release = "24H2",
                Build = 26100,
                UpdateRevision = 1234,
                OsArchitecture = Architecture.Arm64,
                ProcessArchitecture = Architecture.X64,
                UserLocale = "en-nz",
                SystemLocale = "ja-JP",
                DisplayLanguage = "en-GB",
                InstallLanguage = "en-US",
                IsElevated = false,
                IsRemoteSession = true,
            };
        });
        Check(service.SetEnabled(true), "Enabled.");
        service.Track(AnalyticsEvent.AppOpened);
        await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        service.Track(AnalyticsEvent.PageViewed);
        context.Handler.Release.TrySetResult();
        await service.ShutdownAsync();
        var requests = context.Handler.Requests.ToArray();
        Check(requests.Length == 2 && calls == 1, "Only launch events collect Windows context.");
        var values = requests[0].Properties;
        Check(values.EnumerateObject().Count() == 21, "Exactly thirteen compatibility fields plus eight common fields.");
        Check(values.GetProperty("windows_edition").GetString() == "pro_n", "N editions remain distinguishable.");
        Check(values.GetProperty("windows_product_sku").GetUInt32() == 0x31, "SKU retained.");
        Check(values.GetProperty("windows_release").GetString() == "24H2", "Release retained.");
        Check(
            values.GetProperty("windows_build").GetInt32() == 26100 && values.GetProperty("windows_update_revision").GetInt32() == 1234,
            "Full servicing build retained."
        );
        Check(
            values.GetProperty("os_architecture").GetString() == "arm64" && values.GetProperty("process_architecture").GetString() == "x64",
            "OS and process architectures stay distinct."
        );
        Check(
            values.GetProperty("windows_user_locale").GetString() == "en-NZ"
                && values.GetProperty("windows_system_locale").GetString() == "ja-JP",
            "User and system locales stay distinct and canonical."
        );
        Check(
            values.GetProperty("windows_display_language").GetString() == "en-GB"
                && values.GetProperty("windows_install_language").GetString() == "en-US",
            "Display and install languages stay distinct."
        );
        Check(
            !values.GetProperty("is_elevated").GetBoolean() && values.GetProperty("is_remote_session").GetBoolean(),
            "False execution settings remain measurable."
        );
        Check(requests[1].Properties.EnumerateObject().Count() == 8, "Other events do not acquire launch-only properties.");
    }

    private static async Task InvalidValuesAsync()
    {
        using var context = new TestContext();
        using var service = context.Create(windowsContext: () =>
            new AnalyticsWindowsContext
            {
                ProductSku = 9999,
                Release = "synthetic-private-edition",
                Build = -1,
                UpdateRevision = 10_000_001,
                OsArchitecture = (Architecture)999,
                ProcessArchitecture = (Architecture)999,
                UserLocale = "en-x-private-person",
                SystemLocale = "C:\\synthetic-private-path",
                DisplayLanguage = new string('x', 100),
                InstallLanguage = "",
            }
        );
        Check(service.SetEnabled(true), "Enabled.");
        service.Track(AnalyticsEvent.AppOpened);
        await service.ShutdownAsync();
        var request = context.Handler.Requests.Single();
        Check(request.Properties.EnumerateObject().Count() == 10, "Only the numeric SKU and unknown edition survive validation.");
        Check(request.Properties.GetProperty("windows_edition").GetString() == "unknown", "Unrecognized editions are not guessed.");
        Check(request.Properties.GetProperty("windows_product_sku").GetUInt32() == 9999, "Future SKUs remain usable.");
        Check(!request.Raw.Contains("private", StringComparison.Ordinal), "Arbitrary strings cannot leak into compatibility fields.");
    }

    private static async Task UnavailableAsync()
    {
        using var context = new TestContext();
        var calls = 0;
        AnalyticsWindowsContext Fail()
        {
            calls++;
            throw new UnauthorizedAccessException("synthetic-private-error");
        }
        using (var service = context.Create(windowsContext: Fail))
        {
            service.Track(AnalyticsEvent.AppOpened);
            Check(service.SetEnabled(false), "Disabled.");
            service.Track(AnalyticsEvent.AppOpened);
            await service.ShutdownAsync();
        }
        using (var service = context.Create(allowSending: false, windowsContext: Fail))
        {
            Check(service.SetEnabled(true), "Enabled but sending disabled.");
            service.Track(AnalyticsEvent.AppOpened);
            await service.ShutdownAsync();
        }
        Check(calls == 0, "No collection before consent, after opt-out, or when delivery is disabled.");
        using (var service = context.Create(windowsContext: Fail))
        {
            service.Track(AnalyticsEvent.AppOpened);
            await service.ShutdownAsync();
        }
        Check(calls == 1, "Enabled launch attempts collection once.");
        var request = context.Handler.Requests.Single();
        Check(
            request.Properties.EnumerateObject().Count() == 8 && !request.Raw.Contains("private"),
            "Failure still delivers only the base event."
        );
    }

    private static async Task BlockedCollectorAsync()
    {
        using var gate = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var context = new TestContext();
        using var service = context.Create(windowsContext: () =>
        {
            started.TrySetResult();
            gate.Wait();
            return new AnalyticsWindowsContext { ProductSku = 0x65 };
        });
        Check(service.SetEnabled(true), "Enabled.");
        service.Track(AnalyticsEvent.AppOpened);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            await Task.Run(() =>
                {
                    var timer = Stopwatch.StartNew();
                    service.Track(AnalyticsEvent.PageViewed);
                    Check(service.SetEnabled(false), "Opt-out saves during collection.");
                    service.Stop();
                    Check(timer.Elapsed < TimeSpan.FromMilliseconds(500), "Application calls never wait on Windows context collection.");
                })
                .WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            gate.Set();
        }
        await service.ShutdownAsync();
        Check(context.Handler.Requests.IsEmpty, "An event enriched after opt-out is never sent.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
