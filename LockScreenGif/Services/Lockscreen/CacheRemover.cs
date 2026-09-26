namespace LockscreenGif.Services.Lockscreen;

internal sealed class CacheRemover(CacheLayout layout, CachePermissions permissions)
{
    public async Task<DeleteFilesResult> RemoveAsync()
    {
        var result = new DeleteFilesResult();
        var progress = new ApplyProgress(null);
        foreach (var folder in await layout.FindFoldersAsync(progress, CancellationToken.None))
        {
            string[] files;
            try
            {
                files = await layout.FindVariantsAsync(folder, progress, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.FailedDeletions++;
                progress.Report("RemoveFailed", ApplyProgress.Describe(ex), folder, "Error");
                continue;
            }
            foreach (var file in files)
            {
                try
                {
                    permissions.ValidatePath(file);
                    try
                    {
                        Delete(file);
                    }
                    catch (Exception ex) when (CachePermissions.IsAccessDenied(ex))
                    {
                        if (!CacheDeleteAccess.IsDenied(file))
                        {
                            throw new IOException("Windows denied deletion without evidence of missing DELETE access.", ex);
                        }

                        await permissions.GrantAsync(file, true, progress, CancellationToken.None);
                        Delete(file);
                    }
                    result.SuccessfulDeletions++;
                    progress.Report("Removed", "Deleted the GIF cache variant.", file);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result.FailedDeletions++;
                    progress.Report("RemoveFailed", ApplyProgress.Describe(ex), file, "Error");
                }
            }
        }
        return result;
    }

    private static void Delete(string path)
    {
        var attributes = File.GetAttributes(path);
        var wasReadOnly = (attributes & FileAttributes.ReadOnly) != 0;
        if (wasReadOnly)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            if (wasReadOnly)
            {
                try
                {
                    File.SetAttributes(path, attributes);
                }
                catch (Exception ex)
                {
                    Logger.Error("Unable to restore attributes after failed deletion.", ex);
                }
            }
            throw;
        }
    }
}
