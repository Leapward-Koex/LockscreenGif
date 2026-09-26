using System.Security.Cryptography;
using LockscreenGif.Models;

namespace LockscreenGif.Services.Lockscreen;

internal sealed class VerifiedCacheWriter(CachePermissions permissions)
{
    public async Task WriteAsync(
        string sourcePath,
        string sourceHash,
        LockscreenFileResult result,
        ApplyProgress progress,
        CancellationToken cancellationToken
    )
    {
        var stagedPath = Path.Combine(
            Path.GetDirectoryName(result.Path)!,
            $".{Path.GetFileName(result.Path)}.{Guid.NewGuid():N}.lockscreen.tmp"
        );
        progress.Report("Copying", "Staging the selected GIF before replacing the destination.", result.Path);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            permissions.ValidatePath(result.Path);
            permissions.ValidatePath(stagedPath);
            try
            {
                await StageAsync(sourcePath, stagedPath, sourceHash, cancellationToken);
            }
            catch (Exception ex) when (CachePermissions.IsAccessDenied(ex))
            {
                await permissions.GrantAsync(Path.GetDirectoryName(result.Path)!, true, progress, cancellationToken);
                await StageAsync(sourcePath, stagedPath, sourceHash, cancellationToken);
            }
            progress.Report("Staged", "The staged GIF matches the source. Committing the replacement.", result.Path);
            cancellationToken.ThrowIfCancellationRequested();
            var replacement = new CacheFileCommitter(permissions);
            await replacement.CommitAsync(stagedPath, result.Path, progress, cancellationToken);
            result.Copied = true;
            progress.Report("Verifying", "Reading back the committed destination to verify its SHA-256.", result.Path);
            await using var destination = OpenRead(result.Path);
            result.Sha256 = await HashAsync(destination, cancellationToken);
            result.Verified = string.Equals(sourceHash, result.Sha256, StringComparison.OrdinalIgnoreCase);
            result.VerifiedAt = result.Verified ? DateTimeOffset.UtcNow : null;
            result.Error = result.Verified ? null : "The destination hash does not match the source GIF.";
            progress.Report(
                "Verification",
                result.Verified ? "The destination matches the source GIF." : result.Error!,
                result.Path,
                result.Verified ? "Info" : "Error"
            );
        }
        catch (OperationCanceledException ex)
        {
            result.Error = ex.Message;
            throw;
        }
        catch (Exception ex)
        {
            result.Error = ApplyProgress.Describe(ex);
            progress.Report("FileFailed", result.Error, result.Path, "Error");
            Logger.Error($"Failed to apply lockscreen file {result.Path}", ex);
        }
        finally
        {
            try
            {
                permissions.ValidatePath(stagedPath);
                File.Delete(stagedPath);
            }
            catch (Exception ex)
            {
                progress.Report("Cleanup", $"Temporary file cleanup failed: {ApplyProgress.Describe(ex)}", stagedPath, "Warning");
            }
        }
    }

    private static async Task StageAsync(string sourcePath, string stagedPath, string sourceHash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var source = OpenRead(sourcePath);
        await using var staged = new FileStream(
            stagedPath,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 1024 * 1024,
            }
        );
        await source.CopyToAsync(staged, cancellationToken);
        await staged.FlushAsync(cancellationToken);
        staged.Position = 0;
        var stagedHash = await HashAsync(staged, cancellationToken);
        if (!string.Equals(sourceHash, stagedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The staged file does not match the source; the previous destination was preserved.");
        }
    }

    // Denying writes/deletes during a read makes the verification describe one generation.
    public static FileStream OpenRead(string path) =>
        new(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 1024 * 1024,
            }
        );

    public static async Task<string> HashAsync(Stream stream, CancellationToken cancellationToken) =>
        Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
}
