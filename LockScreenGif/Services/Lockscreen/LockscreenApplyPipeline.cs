using System.Buffers.Binary;
using System.Diagnostics;
using LockscreenGif.Models;
using Windows.Storage;
using Windows.System.UserProfile;

namespace LockscreenGif.Services.Lockscreen;

internal sealed class LockscreenApplyPipeline(CacheLayout layout, VerifiedCacheWriter writer)
{
    public async Task<LockscreenApplyResult> ApplyAsync(
        string sourcePath,
        bool useWindowsApi,
        ApplyProgress progress,
        CancellationToken cancellationToken
    )
    {
        var result = new LockscreenApplyResult { ApiRequested = useWindowsApi };
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

            if (useWindowsApi)
            {
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
                await layout.WaitForSettlingAsync(progress, cancellationToken);
            }

            var destinations = await layout.FindDestinationsAsync(progress, cancellationToken);
            if (destinations.Count == 0)
            {
                throw new InvalidOperationException(
                    "No lock-screen cache destinations were found. Set a Picture lock screen in Windows Settings and try again."
                );
            }

            result.Files.AddRange(destinations.Select(path => new LockscreenFileResult { Path = path, Error = "Not attempted." }));
            foreach (var target in result.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteAsync(sourcePath, hash, target, progress, cancellationToken);
            }
            result.Success = result.Files.All(file => file.Copied && file.Verified);
            result.Error = result.Success
                ? null
                : $"Only {result.Files.Count(file => file.Verified)} of {result.Files.Count} destinations were verified.";
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
            result.Error = ex.Message;
            progress.Report("Cancelled", ex.Message, severity: "Warning");
        }
        catch (Exception ex)
        {
            result.Error = ApplyProgress.Describe(ex);
            progress.Report("Failed", result.Error, severity: "Error");
            Logger.Error("Lockscreen apply failed", ex);
        }
        return result;
    }
}
