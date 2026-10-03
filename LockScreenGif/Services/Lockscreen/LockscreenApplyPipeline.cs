using System.Buffers.Binary;
using System.Diagnostics;
using LockscreenGif.Models;
using LockscreenGif.Privileged;
using Windows.Storage;
using Windows.System.UserProfile;

namespace LockscreenGif.Services.Lockscreen;

internal sealed class LockscreenApplyPipeline(
    CacheLayout layout,
    VerifiedCacheWriter writer,
    Func<WindowsImageFeatureState> readWindowsImageFeature
)
{
    public async Task<LockscreenApplyResult> ApplyAsync(
        string sourcePath,
        bool useWindowsApi,
        ApplyProgress progress,
        CancellationToken cancellationToken
    )
    {
        var result = new LockscreenApplyResult { ApiRequested = useWindowsApi };
        var failureStage = LockscreenApplyFailureReason.SourceReadFailed;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Hold the source open throughout the operation. All target copies and the
            // optional Windows API call then use the exact source whose hash was recorded.
            await using var source = VerifiedCacheWriter.OpenRead(sourcePath);
            var sourceLength = source.Length;
            var header = new byte[sourceLength >= 10 ? 10 : 6];
            await source.ReadExactlyAsync(header, cancellationToken);
            var signature = System.Text.Encoding.ASCII.GetString(header, 0, 6);
            if (signature is not "GIF87a" and not "GIF89a")
            {
                throw new InvalidDataException("The selected source is not a GIF file.");
            }

            result.SourceSizeBytes = sourceLength;
            if (header.Length >= 10)
            {
                // GIF logical-screen dimensions follow the six-byte signature, as little-endian unsigned words.
                // Reuse the header read; do not decode or reopen the source just for analytics.
                var width = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6, 2));
                var height = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8, 2));
                if (width > 0 && height > 0)
                {
                    result.SourceWidth = width;
                    result.SourceHeight = height;
                }
            }

            source.Position = 0;
            var hash = await VerifiedCacheWriter.HashAsync(source, cancellationToken);
            progress.Report("Source", $"Source verified. Size={source.Length} bytes; SHA256={hash}.", sourcePath);

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                result.WindowsImageFeatureAtApply = readWindowsImageFeature();
            }
            catch (Exception ex)
            {
                // Configuration evidence is read-only and must not prevent copying the GIF.
                result.WindowsImageFeatureAtApply = new WindowsImageFeatureState
                {
                    ObservedAt = DateTimeOffset.UtcNow,
                    QueryError = ApplyProgress.Describe(ex),
                };
            }
            progress.Report(
                "WindowsImageFeature",
                $"Windows feature configuration at apply: {WindowsImageFeature.Describe(result.WindowsImageFeatureAtApply)}",
                severity: result.WindowsImageFeatureAtApply.QueryStatus != 0
                || result.WindowsImageFeatureAtApply.QueryError is not null
                || result.WindowsImageFeatureAtApply.OverrideError is not null
                    ? "Warning"
                    : "Info"
            );
            cancellationToken.ThrowIfCancellationRequested();

            if (useWindowsApi)
            {
                failureStage = LockscreenApplyFailureReason.WindowsApiFailed;
                progress.Report("WindowsApi", "Setting the lock-screen image through Windows before replacing cache files.");
                var apiTimer = Stopwatch.StartNew();
                var file = await StorageFile.GetFileFromPathAsync(sourcePath).AsTask(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                // Once Windows starts changing the image, await its completion even if
                // cancelled so another session cannot overlap an in-flight OS mutation.
                var settingImage = LockScreen.SetImageFileAsync(file).AsTask();
                using (
                    cancellationToken.Register(() =>
                        progress.Report(
                            "Cancelling",
                            "Cancellation requested. Waiting for Windows to finish setting the image.",
                            severity: "Warning"
                        )
                    )
                )
                {
                    await settingImage;
                }
                result.ApiCompleted = true;
                progress.Report(
                    "WindowsApi",
                    $"Windows accepted the image-setting request in {apiTimer.ElapsedMilliseconds} ms. Animated playback is not guaranteed."
                );
                cancellationToken.ThrowIfCancellationRequested();
                failureStage = LockscreenApplyFailureReason.CacheDiscoveryFailed;
                await layout.WaitForSettlingAsync(progress, cancellationToken);
            }

            failureStage = LockscreenApplyFailureReason.CacheDiscoveryFailed;
            var destinations = await layout.FindDestinationsAsync(progress, cancellationToken);
            if (destinations.Count == 0)
            {
                failureStage = LockscreenApplyFailureReason.NoDestinations;
                throw new InvalidOperationException(
                    "No lock-screen cache destinations were found. Set a Picture lock screen in Windows Settings and try again."
                );
            }

            result.Files.AddRange(destinations.Select(path => new LockscreenFileResult { Path = path, Error = "Not attempted." }));
            failureStage = LockscreenApplyFailureReason.CopyFailed;
            foreach (var target in result.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteAsync(sourcePath, hash, target, progress, cancellationToken);
                result.FailureException ??= target.FailureException;
                if (!target.Copied || !target.Verified)
                {
                    // Keep the first unsuccessful destination aligned with the retained exception.
                    result.FailureReason ??=
                        target.Copied ? LockscreenApplyFailureReason.VerificationFailed
                        : target.FailureException is { } error && CachePermissions.IsAccessDenied(error)
                            ? LockscreenApplyFailureReason.CacheInaccessible
                        : LockscreenApplyFailureReason.CopyFailed;
                }
            }
            result.Success = result.Files.All(file => file.Copied && file.Verified);
            result.Error = result.Success
                ? null
                : $"Only {result.Files.Count(file => file.Verified)} of {result.Files.Count} destinations were verified. "
                    + RecoveryMessage(result.FailureReason ?? LockscreenApplyFailureReason.Unknown);
            progress.Report(
                "Completed",
                result.Success
                    ? $"All {result.Files.Count} destinations were verified. Lock the computer to check playback."
                    : result.Error!,
                severity: result.Success ? "Info" : "Error"
            );
        }
        catch (OperationCanceledException ex)
        {
            result.Cancelled = true;
            result.FailureReason = null;
            result.Error = ex.Message;
            progress.Report("Cancelled", ex.Message, severity: "Warning");
        }
        catch (Exception ex)
        {
            result.FailureException = ex;
            result.FailureReason = ClassifyFailure(failureStage, ex);
            result.Error = RecoveryMessage(result.FailureReason.Value);
            // Keep technical details in local evidence, separate from the actionable UI message.
            progress.Report("Failed", $"{result.Error} {ApplyProgress.Describe(ex)}", severity: "Error");
            Logger.Error("Lockscreen apply failed", ex);
        }
        return result;
    }

    private static LockscreenApplyFailureReason ClassifyFailure(LockscreenApplyFailureReason stage, Exception error) =>
        stage switch
        {
            LockscreenApplyFailureReason.SourceReadFailed when error is InvalidDataException or EndOfStreamException =>
                LockscreenApplyFailureReason.InvalidSource,
            LockscreenApplyFailureReason.CacheDiscoveryFailed when CachePermissions.IsAccessDenied(error) =>
                LockscreenApplyFailureReason.CacheInaccessible,
            LockscreenApplyFailureReason.CacheDiscoveryFailed when error is DirectoryNotFoundException or FileNotFoundException =>
                LockscreenApplyFailureReason.CacheMissing,
            _ => stage,
        };

    private static string RecoveryMessage(LockscreenApplyFailureReason reason) =>
        reason switch
        {
            LockscreenApplyFailureReason.CacheInaccessible =>
                "Windows denied access to the lock-screen cache. Retry and allow the Windows permission request if it appears. "
                    + "If access is still denied, export a diagnostic report for support or your administrator.",
            LockscreenApplyFailureReason.CacheMissing =>
                "The Windows lock-screen cache could not be found. Open Windows Settings > Personalization > Lock screen, "
                    + "choose Picture and select an image, then retry. If the cache is still unavailable, export a diagnostic report for support.",
            LockscreenApplyFailureReason.NoDestinations =>
                "No lock-screen cache destinations were found. Open Windows Settings > Personalization > Lock screen, "
                    + "choose Picture and select an image, then retry. If no destinations appear, export a diagnostic report for support.",
            LockscreenApplyFailureReason.CacheDiscoveryFailed =>
                "The lock-screen cache could not be inspected. Retry, then export a diagnostic report for support if the problem continues.",
            LockscreenApplyFailureReason.InvalidSource => "The selected file is not a readable GIF. Choose another GIF and retry.",
            LockscreenApplyFailureReason.SourceReadFailed =>
                "The selected GIF could not be read. Check that the file is available and readable, then select it again.",
            LockscreenApplyFailureReason.WindowsApiFailed =>
                "Windows could not set the lock-screen image. Open Windows Settings > Personalization > Lock screen, "
                    + "choose Picture and select an image, then retry. If Windows prevents this change, contact your administrator.",
            LockscreenApplyFailureReason.CopyFailed =>
                "The GIF could not be copied to every lock-screen cache file. Retry, then export a diagnostic report for support if the problem continues.",
            LockscreenApplyFailureReason.VerificationFailed =>
                "A copied lock-screen cache file could not be verified. Retry, then export a diagnostic report for support if the problem continues.",
            _ => "The GIF could not be applied. Export a diagnostic report for support.",
        };
}
