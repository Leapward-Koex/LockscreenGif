using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using LockscreenGif.Services.Lockscreen;

internal static class PermissionSessionTests
{
    public static async Task ScopeAndLifetime()
    {
        using var fixture = new CacheFixture();
        var requests = new List<(string Path, bool Write)>();
        var helper = new FakeSession(requests);
        var created = 0;
        await using (
            var permissions = new CachePermissions(
                fixture.Root,
                CurrentSid(),
                repairSessionFactory: () =>
                {
                    created++;
                    return helper;
                }
            )
        )
        {
            Check(created == 0, "Ordinary reads must not launch a permission helper.");
            await permissions.GrantAsync(fixture.Folder, false, new(null), default);
            await permissions.GrantAsync(fixture.MainImage, true, new(null), default);
            Check(
                created == 1 && requests.SequenceEqual(new[] { (fixture.Folder, false), (fixture.MainImage, true) }),
                "Only failed paths should reach one shared helper, with their original access levels."
            );
            try
            {
                await permissions.GrantAsync(Path.Combine(fixture.Root, "..", "outside"), true, new(null), default);
            }
            catch (InvalidOperationException) { }
            Check(requests.Count == 2, "An out-of-scope path must never reach the helper.");
        }
        Check(helper.Disposed, "The operation must dispose the permission helper.");
        var borrowed = new FakeSession([]);
        await using (var permissions = new CachePermissions(fixture.Root, CurrentSid(), borrowedSession: borrowed))
        {
            await permissions.GrantAsync(fixture.MainImage, true, new(null), default);
        }

        Check(!borrowed.Disposed, "Applying must not dispose a session owned by diagnostics.");
        await borrowed.DisposeAsync();
    }

    public static async Task DeclinedElevationIsNotRepeated()
    {
        using var fixture = new CacheFixture();
        var launches = 0;
        await using var session = new CachePermissionSession(
            fixture.Root,
            CurrentSid(),
            _ =>
            {
                launches++;
                throw new Win32Exception(1223);
            }
        );
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await session.GrantAsync(fixture.Folder, true, default);
                throw new Exception("Cancellation was ignored.");
            }
            catch (OperationCanceledException) { }
        }
        Check(launches == 1, "Declining UAC must not prompt again later in the same operation.");
    }

    public static async Task FailedLaunchIsNotRepeated()
    {
        using var fixture = new CacheFixture();
        var launches = 0;
        await using var session = new CachePermissionSession(
            fixture.Root,
            CurrentSid(),
            _ =>
            {
                launches++;
                throw new InvalidOperationException("Simulated launch failure");
            }
        );
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await session.GrantAsync(fixture.Folder, true, default);
                throw new Exception("Failure was ignored.");
            }
            catch (InvalidOperationException ex) when (ex.Message == "Simulated launch failure") { }
        }
        Check(launches == 1, "A broken helper launch must not create a UAC loop.");
    }

    public static async Task PrecancelledRepairDoesNotLaunch()
    {
        using var fixture = new CacheFixture();
        var launches = 0;
        await using var session = new CachePermissionSession(
            fixture.Root,
            CurrentSid(),
            _ =>
            {
                launches++;
                throw new Exception("Must not launch");
            }
        );
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await session.GrantAsync(fixture.Folder, true, cancelled.Token);
            throw new Exception("Cancellation ignored.");
        }
        catch (OperationCanceledException) { }
        Check(launches == 0, "A cancelled operation must not request elevation.");
    }

    internal static string CurrentSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!.Value;
    }

    internal static void Check(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FakeSession(List<(string Path, bool Write)> requests) : ICachePermissionSession
    {
        public bool Disposed { get; private set; }

        public Task<int> GrantAsync(string path, bool write, CancellationToken token)
        {
            requests.Add((path, write));
            return Task.FromResult(0);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
