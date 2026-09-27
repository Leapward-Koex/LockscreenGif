namespace LockscreenGif.Services.Lockscreen;

/// <summary>Commits a verified sibling file without exposing a partially copied destination.</summary>
internal sealed class CacheFileCommitter(CachePermissions permissions)
{
    public async Task CommitAsync(string stagedPath, string destinationPath, ApplyProgress progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await permissions.ValidateWithRepairAsync(destinationPath, true, progress, cancellationToken);
        permissions.ValidatePath(stagedPath);
        try
        {
            Move(stagedPath, destinationPath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException || CachePermissions.IsAccessDenied(ex))
        {
            // Staging already established parent-folder write access. A rename can still
            // return ACCESS_DENIED for an open destination, so probe DELETE before UAC.
            var deleteDenied = CacheDeleteAccess.IsDenied(destinationPath);
            if (!CachePermissions.IsAccessDenied(ex))
            {
                throw;
            }

            if (!deleteDenied)
            {
                throw new IOException(
                    "Windows denied the replacement even though missing DELETE access was not established. The previous image was preserved.",
                    ex
                );
            }

            await permissions.GrantAsync(destinationPath, true, progress, cancellationToken);
            permissions.ValidatePath(stagedPath);
            permissions.ValidatePath(destinationPath);
            Move(stagedPath, destinationPath, cancellationToken);
        }
    }

    private static void Move(string stagedPath, string destinationPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileAttributes? originalAttributes = null;
        var committed = false;
        try
        {
            if (File.Exists(destinationPath))
            {
                var attributes = File.GetAttributes(destinationPath);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(destinationPath, attributes & ~FileAttributes.ReadOnly);
                    originalAttributes = attributes;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Replacing an existing file atomically preserves its ACL and Windows metadata.
            if (File.Exists(destinationPath))
            {
                File.Replace(stagedPath, destinationPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(stagedPath, destinationPath);
            }

            committed = true;
        }
        finally
        {
            if (!committed && originalAttributes.HasValue)
            {
                try
                {
                    File.SetAttributes(destinationPath, originalAttributes.Value);
                }
                catch (Exception ex)
                {
                    Logger.Error("Unable to restore cache file attributes after a failed replacement.", ex);
                }
            }
        }
    }
}
