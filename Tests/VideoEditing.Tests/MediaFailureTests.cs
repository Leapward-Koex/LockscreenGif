using System.Diagnostics;
using GifskiNet;
using LockscreenGif.Services;

internal static class MediaFailureTests
{
    public static async Task RunAsync(string validPng)
    {
        CheckEncoderLifecycle();
        var root = TempDirectoryService.GetAppTempRoot();
        var invalidVideo = Path.Combine(root, "synthetic-private-invalid-video.mkv");
        await File.WriteAllTextAsync(invalidVideo, "This is deliberately not a video.");
        try
        {
            await VideoFrameService.IndexAsync(invalidVideo, 30, null, CancellationToken.None);
            throw new InvalidOperationException("FAILED: Invalid video was accepted.");
        }
        catch (MediaProcessingException ex)
        {
            Check(ex.Component == MediaProcessingComponent.Ffmpeg && ex.ErrorCode != 0, "FFmpeg retains its nonzero native exit code");
            Check(
                ex.Message.Contains("synthetic-private-invalid-video", StringComparison.Ordinal),
                "FFmpeg keeps full diagnostics in local exception text"
            );
        }

        var invalidFrames = Path.Combine(root, "invalid-png-frames");
        Directory.CreateDirectory(invalidFrames);
        // Keep a real header so the native decoder, rather than our dimension reader, rejects the image.
        var header = (await File.ReadAllBytesAsync(validPng))[..24];
        await File.WriteAllBytesAsync(Path.Combine(invalidFrames, "frame_000001.png"), header);
        try
        {
            await GifSkiService.CreateGif(invalidFrames, _ => { }, [0], 0.1);
            throw new InvalidOperationException("FAILED: Invalid PNG was accepted.");
        }
        catch (MediaProcessingException ex)
        {
            Check(
                ex.Component == MediaProcessingComponent.Gifski
                    && ex.ErrorCode != (int)GifskiError.OK
                    && Enum.IsDefined((GifskiError)ex.ErrorCode),
                "Gifski preserves the native code when rejecting a corrupt input frame"
            );
        }
        CheckLibraryLifetime();
    }

    public static void CheckLibraryLifetime()
    {
        // Gifski's Rust worker teardown can outlive Finish. This check does not
        // acquire a library reference itself, which would hide premature unloading.
        var expected = Path.Combine(AppContext.BaseDirectory, "Vendor", "gifski", "gifski.dll");
        using var process = Process.GetCurrentProcess();
        Check(
            process
                .Modules.Cast<ProcessModule>()
                .Any(module => string.Equals(module.FileName, expected, StringComparison.OrdinalIgnoreCase)),
            "Gifski module remains loaded after encoder success or failure while native workers finish exiting"
        );
    }

    private static void CheckEncoderLifecycle()
    {
        var firstError = new MediaProcessingException(
            MediaProcessingComponent.Gifski,
            (int)GifskiError.INVALID_INPUT,
            "Synthetic frame failure"
        );
        foreach (var cleanupThrows in new[] { false, true })
        {
            var finishes = 0;
            Exception? actual = null;
            try
            {
                GifSkiService.SubmitAndFinish(
                    () => throw firstError,
                    () =>
                    {
                        finishes++;
                        if (cleanupThrows)
                        {
                            throw new InvalidOperationException("Synthetic cleanup failure");
                        }
                        return GifskiError.INVALID_STATE;
                    }
                );
            }
            catch (Exception ex)
            {
                actual = ex;
            }
            Check(ReferenceEquals(actual, firstError), "Cleanup preserves the original submission error");
            Check(finishes == 1, "Failed setup or submission finishes the native encoder exactly once");
        }

        foreach (var finalResult in new[] { GifskiError.OK, GifskiError.INVALID_STATE })
        {
            var finishes = 0;
            var submitted = false;
            Exception? actual = null;
            try
            {
                GifSkiService.SubmitAndFinish(
                    () => submitted = true,
                    () =>
                    {
                        Check(submitted, "Native finalization follows frame submission");
                        finishes++;
                        return finalResult;
                    }
                );
            }
            catch (Exception ex)
            {
                actual = ex;
            }
            Check(finishes == 1, "Native finalization is never retried after it consumes the handle");
            Check(
                finalResult == GifskiError.OK
                    ? actual is null
                    : actual is MediaProcessingException media && media.ErrorCode == (int)finalResult,
                "Finalization returns success or preserves its native error code"
            );
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("FAILED: " + message);
        }

        Console.WriteLine("PASS " + message);
    }
}
