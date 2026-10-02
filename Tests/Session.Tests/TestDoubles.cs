using System.Security.Cryptography;
using LockscreenGif.Models;

namespace LockscreenGif.Contracts.Services
{
    public sealed record SourceFile(string Path);

    public interface ILockscreenService
    {
        SourceFile? CurrentImage { get; set; }
        LockscreenSource? CurrentSource { get; }
        string CacheDirectory { get; }
        bool IsApplying { get; }
        Task WaitForIdleAsync();
        Task<LockscreenApplyResult> ApplyAsync(
            string sourcePath,
            bool useWindowsApi,
            Action<LockscreenApplyEvent>? progress = null,
            CancellationToken cancellationToken = default,
            LockscreenGif.Services.Lockscreen.ICachePermissionSession? permissionSession = null,
            LockscreenSourceKind sourceKind = LockscreenSourceKind.Unknown
        );
    }

    internal sealed class FakeLockscreenService(string cacheDirectory) : ILockscreenService
    {
        public SourceFile? CurrentImage
        {
            get => CurrentSource is { } source ? new(source.Path) : null;
            set => CurrentSource = value is null ? null : new(value.Path, LockscreenSourceKind.UserGif);
        }
        public LockscreenSource? CurrentSource { get; set; }
        public string CacheDirectory { get; } = cacheDirectory;
        public bool IsApplying { get; private set; }
        public bool BlockApply { get; set; }
        public bool DeferCancellation { get; set; }
        public LockscreenGif.Privileged.WindowsImageFeatureState? WindowsImageFeatureAtApply { get; set; }
        public TaskCompletionSource ReleaseApply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ApplyEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(byte[] Bytes, bool UseWindowsApi, string SourcePath, LockscreenSourceKind SourceKind)> Applies { get; } = [];

        public async Task WaitForIdleAsync()
        {
            while (IsApplying)
            {
                await Task.Delay(10);
            }
        }

        public async Task<LockscreenApplyResult> ApplyAsync(
            string sourcePath,
            bool useWindowsApi,
            Action<LockscreenApplyEvent>? progress = null,
            CancellationToken cancellationToken = default,
            LockscreenGif.Services.Lockscreen.ICachePermissionSession? permissionSession = null,
            LockscreenSourceKind sourceKind = LockscreenSourceKind.Unknown
        )
        {
            IsApplying = true;
            try
            {
                var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
                Applies.Add((bytes, useWindowsApi, sourcePath, sourceKind));
                ApplyEntered.TrySetResult();
                if (DeferCancellation)
                {
                    await ReleaseApply.Task;
                }
                else if (BlockApply)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                var destination = Path.Combine(CacheDirectory, "LockScreen_1920_1080_notdimmed.jpg");
                await File.WriteAllBytesAsync(destination, bytes, cancellationToken);
                var sha = Convert.ToHexString(SHA256.HashData(bytes));
                progress?.Invoke(
                    new()
                    {
                        Stage = "Copy",
                        Message = "Test cache copy verified",
                        Path = destination,
                    }
                );
                return new()
                {
                    Success = true,
                    WindowsImageFeatureAtApply = WindowsImageFeatureAtApply,
                    ApiRequested = useWindowsApi,
                    ApiCompleted = useWindowsApi,
                    Files =
                    [
                        new()
                        {
                            Path = destination,
                            Copied = true,
                            Verified = true,
                            Sha256 = sha,
                        },
                    ],
                };
            }
            finally
            {
                IsApplying = false;
            }
        }
    }
}

namespace LockscreenGif.Services.Diagnostics
{
    public sealed class WindowsSessionMonitor
    {
        public bool IsRegistered { get; set; } = true;
        public int SessionId { get; set; } = 1;
        public bool LockSucceeds { get; set; } = true;
        public int LockRequests { get; private set; }
        public bool PowerNotificationsAvailable { get; set; } = true;
        public string? PowerError { get; set; }
        public string? Error { get; set; }
        public event Action<string>? Observed;

        public void Emit(string name) => Observed?.Invoke(name);

        public bool TryLock(out string? error)
        {
            LockRequests++;
            error = LockSucceeds ? null : "Synthetic lock request failure";
            return LockSucceeds;
        }
    }

    public static class EnvironmentCollector
    {
        public static Dictionary<string, string> Collect(string directory) =>
            new() { ["Cache directory"] = directory, ["Application"] = "Session test harness; native monitoring is stubbed" };
    }
}

public static class Logger
{
    public static void Info(string message) => Console.WriteLine("LOG " + message);

    // This absent synthetic directory prevents session exports from touching the developer's real application logs.
    private static readonly string LogPath = Path.Combine(
        Path.GetTempPath(),
        "LockscreenGif-absent-test-logs-" + Guid.NewGuid().ToString("N")
    );

    public static string GetLogPath() => LogPath;

    public static void Warn(string message) => Console.WriteLine("LOG " + message);
}
