using Windows.Storage;
using Windows.Storage.Provider;

namespace LockscreenGif.Services;

internal static class PickedFileWriter
{
    public static async Task CopyAsync(StorageFile source, StorageFile destination)
    {
        if (source.IsEqual(destination))
        {
            return;
        }

        // Plain filesystem files have no provider app to notify. CompleteUpdatesAsync
        // can fail with element-not-found for these files in an unpackaged desktop app.
        var providerId = destination.Provider?.Id;
        if (
            string.Equals(providerId, "computer", StringComparison.OrdinalIgnoreCase)
            || string.Equals(providerId, "local", StringComparison.OrdinalIgnoreCase)
        )
        {
            await CopyContentsAsync(source, destination);
            return;
        }

        CachedFileManager.DeferUpdates(destination);
        var writeFailed = false;
        try
        {
            await CopyContentsAsync(source, destination);
        }
        catch
        {
            writeFailed = true;
            throw;
        }
        finally
        {
            try
            {
                // Close both streams before allowing the file provider to synchronize.
                var status = await CachedFileManager.CompleteUpdatesAsync(destination);
                if (status is not FileUpdateStatus.Complete and not FileUpdateStatus.CompleteAndRenamed)
                {
                    throw new IOException($"The selected location could not finish saving the file ({status}).");
                }
            }
            catch (Exception ex) when (writeFailed)
            {
                // Keep the original write/open/flush failure as the operation's error.
                Logger.Error("Completing picked-file updates also failed after writing failed.", ex);
            }
        }
    }

    private static async Task CopyContentsAsync(StorageFile source, StorageFile destination)
    {
        // Write into the picker-owned file so provider metadata stays attached to it.
        using var input = await source.OpenStreamForReadAsync();
        using var output = await destination.OpenStreamForWriteAsync();
        output.SetLength(0);
        output.Position = 0;
        await input.CopyToAsync(output);
        await output.FlushAsync();
    }
}
