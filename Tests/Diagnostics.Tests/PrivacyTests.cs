using System.Text.Json;
using LockscreenGif.Models.Diagnostics;
using Lockscreen = LockscreenGif.Services.Diagnostics;

namespace Diagnostics.Tests;

internal static class PrivacyTests
{
    internal static void Run()
    {
        var session = new DiagnosticSession
        {
            SourceName = "My private family.gif",
            SourcePath = @"D:\Media\My private family.gif",
            Environment = new()
            {
                ["Cache directory"] = @"C:\SystemData\S-1-5-21-444-555-666-1002\ReadOnly",
                ["Machine policy / LockScreenImage"] = "https://private.example/alice/family.jpg?token=secret",
                ["User policy / LockScreenImage"] = "Bare personal image.jpg",
                ["Original lock-screen image (may be unavailable)"] = "ms-appx:///Assets/Initial private image.jpg",
            },
            EnvironmentObservations =
            [
                new(
                    DateTimeOffset.UtcNow,
                    "AfterApply",
                    new()
                    {
                        ["Machine policy / LockScreenImage"] = "Later personal image.jpg",
                        ["Original lock-screen image (may be unavailable)"] = "ms-appdata:///Local/SecretUser/private.gif",
                    }
                ),
                new(DateTimeOffset.UtcNow, "AfterUnlock", new() { ["Original lock-screen image (may be unavailable)"] = "Not available" }),
            ],
        };
        var redactor = new Lockscreen.DiagnosticRedactor(session);
        var sensitive = new[]
        {
            session.SourceName,
            session.SourcePath,
            "https://another-private.example/user:password/image.jpg?secret=123",
            session.Environment["Machine policy / LockScreenImage"],
            session.Environment["User policy / LockScreenImage"],
            @"\\PRIVATE-SERVER\SecretShare\SecretFamily.gif",
            @"C:\Others\LockScreen_SecretFamily.gif",
            @"C:\SystemData\S-1-5-21-444-555-666-1002\ReadOnly\SecretPerson\LockScreen_SecretFamily.gif",
            "S-1-5-21-999-888-777-1005",
            "Later personal image.jpg",
            "ms-appdata:///Local/SecretUser/private.gif",
            "ms-appx:///Assets/Initial private image.jpg",
        };
        foreach (var value in sensitive)
        {
            var result = redactor.Redact(value);
            Program.Check(
                result != value
                    && !result.Contains("Secret")
                    && !result.Contains("private", StringComparison.OrdinalIgnoreCase)
                    && !result.Contains("personal", StringComparison.OrdinalIgnoreCase)
                    && !result.Contains("S-1-5-21"),
                "Redacts sensitive value: " + Array.IndexOf(sensitive, value)
            );
        }
        var publicCache = redactor.Redact(
            @"C:\SystemData\S-1-5-21-444-555-666-1002\ReadOnly\LockScreen_A\LockScreen___1920_1080_notdimmed.jpg"
        );
        Program.Check(publicCache == "<cache>/LockScreen_A/LockScreen___1920_1080_notdimmed.jpg", "Known cache names remain recognizable");
        var firstSid = redactor.Redact("S-1-5-21-999-888-777-1005");
        Program.Check(firstSid == redactor.Redact("S-1-5-21-999-888-777-1005"), "Other-user SID aliases remain consistent");
        var json = redactor.Redact(JsonSerializer.SerializeToNode(session))!.ToJsonString();
        Program.Check(!json.Contains("token=secret") && !json.Contains("Bare personal"), "Policy values redacted in structured reports");
        Program.Check(
            !json.Contains("Later personal")
                && !json.Contains("SecretUser")
                && !json.Contains("ms-appdata")
                && !json.Contains("Initial private"),
            "Later policy values and custom-scheme original image URIs are redacted"
        );
        Program.Check(
            redactor.Redact("Not available") == "Not available" && json.Contains("Not available"),
            "Unavailable original-image status remains readable"
        );
    }
}
