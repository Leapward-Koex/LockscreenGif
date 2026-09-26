namespace LockscreenGif.Services.Lockscreen;

/// <summary>Repairs access only for a failed apply/remove operation, never for diagnostic reads.</summary>
internal sealed class CachePermissions(
    string cacheDirectory,
    string userSid,
    bool allowElevation = true,
    Func<ICachePermissionSession>? repairSessionFactory = null,
    ICachePermissionSession? borrowedSession = null
) : IAsyncDisposable
{
    private readonly string _root = Path.GetFullPath(cacheDirectory).TrimEnd(Path.DirectorySeparatorChar);

    public void ValidatePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (
            !fullPath.Equals(_root, StringComparison.OrdinalIgnoreCase)
            && !fullPath.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new InvalidOperationException("The operation is outside the current user's lock-screen cache.");
        }

        // Never follow a junction or symlink out of the cache when granting access or writing.
        for (var current = fullPath; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException("Reparse points are not supported in the lock-screen cache.");
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (UnauthorizedAccessException ex)
            {
                throw new UnauthorizedAccessException(
                    $"Windows denied reading path attributes for '{current}', so the cache scope cannot be checked for links. "
                        + "No permissions were changed. Export the diagnostic report so an administrator can review access to this user's cache.",
                    ex
                );
            }
            // Include ancestors above the cache root: a junction on the SID folder also escapes the intended tree.
        }
    }

    private readonly bool _ownsSession = borrowedSession is null;
    private ICachePermissionSession? _session = borrowedSession;

    public async Task GrantAsync(string path, bool write, ApplyProgress progress, CancellationToken cancellationToken)
    {
        if (!allowElevation)
        {
            throw new UnauthorizedAccessException("Permission repair is disabled for this isolated operation.");
        }

        ValidatePath(path);
        cancellationToken.ThrowIfCancellationRequested();
        var firstRequest = _session is null;
        _session ??= repairSessionFactory?.Invoke() ?? new CachePermissionSession(_root, userSid);
        progress.Report(
            "Permissions",
            firstRequest
                ? "Access was denied. Requesting permission once for this operation's required cache repairs."
                : "Reusing the approved permission helper for this path; no additional elevation is requested.",
            path
        );
        var exitCode = await _session.GrantAsync(path, write, cancellationToken);
        progress.Report("Permissions", $"Path permission repair exited with code {exitCode}.", path, exitCode == 0 ? "Info" : "Warning");
        if (exitCode != 0)
        {
            throw new UnauthorizedAccessException($"Cache permission repair failed with exit code {exitCode}.");
        }
    }

    public ValueTask DisposeAsync() => _ownsSession ? _session?.DisposeAsync() ?? ValueTask.CompletedTask : ValueTask.CompletedTask;

    public static bool IsAccessDenied(Exception ex) => ex is UnauthorizedAccessException || ex.HResult == unchecked((int)0x80070005);
}
