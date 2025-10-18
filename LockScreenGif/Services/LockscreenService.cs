using LockscreenGif.Contracts.Services;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using System.Security.AccessControl;
using Path = System.IO.Path;

namespace LockscreenGif.Services;

public class DeleteFilesResult
{
    public int SuccessfulDeletions = 0;
    public int FailedDeletions = 0;
}

public sealed class LockscreenService : ILockscreenService
{
    private const string LockscreenRoot = @"C:\ProgramData\Microsoft\Windows\SystemData";
    private const string DimmedSuffix = "_notdimmed.jpg";
    private const string DimmedPattern = "*_notdimmed.jpg";
    private const string DimmedBaseName = "LockScreen.jpg";

    private readonly SecurityIdentifier _sid =
        WindowsIdentity.GetCurrent().User
        ?? throw new InvalidOperationException("Unable to obtain user SID.");

    public StorageFile? CurrentImage
    {
        get; set;
    }

    public BitmapImage? CurrentImageBitmap =>
        CurrentImage is null ? null : new BitmapImage { UriSource = new Uri(CurrentImage.Path) };

    private string LockscreenDirectory => Path.Combine(LockscreenRoot, _sid.Value, "ReadOnly");

    /*------------------------------------------------------------------
     * PUBLIC API
     *----------------------------------------------------------------*/

   public async Task<bool> ApplyGifAsLockscreenAsync()
{
    try
    {
        Logger.Info($"User SID: {_sid}. CurrentImage: {CurrentImage?.Path}");

        if (CurrentImage is null)
        {
            Logger.Warn("No current image found; aborting ApplyGifAsLockscreenAsync");
            return false;
        }

        // Save a copy of the image in a permanent, stable folder
        string permanentFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "LockscreenGif"
        );

        Directory.CreateDirectory(permanentFolder);
        string permanentImagePath = Path.Combine(permanentFolder, "wallpaper.jpg");

        await CurrentImage.CopyAsync(
            await StorageFolder.GetFolderFromPathAsync(permanentFolder),
            "wallpaper.jpg",
            NameCollisionOption.ReplaceExisting
        );

        Logger.Info($"Saved lockscreen image permanently at {permanentImagePath}");

        //Update registry so Windows remembers the image
        SetLockscreenRegistry(permanentImagePath);

        // Still copy to SystemData (optional for immediate visual sync)
        await EnsureFolderWritableAsync(LockscreenDirectory);
        await CreateDimmedFilesAsync(LockscreenDirectory);
        await LogFilesWithMimeTypesAsync(LockscreenDirectory);

        Logger.Info("Successfully set and persisted lockscreen.");
        return true;
    }
    catch (Exception ex)
    {
        Logger.Error("Failed to set lockscreen", ex);
        return false;
    }
}


private static void SetLockscreenRegistry(string imagePath)
{
    try
    {
        const string keyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP";
        const string imageKey = "LockScreenImagePath";
        const string imageUrlKey = "LockScreenImageUrl";
        const string statusKey = "LockScreenImageStatus";

        using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(keyPath, true);
        key?.SetValue(imageKey, imagePath, Microsoft.Win32.RegistryValueKind.String);
        key?.SetValue(imageUrlKey, imagePath, Microsoft.Win32.RegistryValueKind.String);
        key?.SetValue(statusKey, 1, Microsoft.Win32.RegistryValueKind.DWord);

        Logger.Info($"Lockscreen registry updated → {imagePath}");
    }
    catch (Exception ex)
    {
        Logger.Error("Failed to set lockscreen registry keys", ex);
    }
}

    public async Task<DeleteFilesResult?> RemoveAppliedGif()
    {
        try
        {
            Logger.Info($"User SID: {_sid}");

            await EnsureFolderWritableAsync(LockscreenDirectory);

            var result = DeleteDimmedFiles(LockscreenDirectory);
            Logger.Info($"Deleted dimmed files. {result.SuccessfulDeletions} success, {result.FailedDeletions} failures.");
            return result;
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to delete dimmed lockscreen files", ex);
            return null;
        }
    }

    /*------------------------------------------------------------------
     *   PER-FOLDER ACCESS HELPERS
     *----------------------------------------------------------------*/

    private static bool HasWriteAccess(string directory)
    {
        try
        {
            var test = Path.Combine(directory, $"write_test_{Guid.NewGuid():N}.tmp");
            using (File.Create(test, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task EnsureFolderWritableAsync(string directory)
    {
        Logger.Info("Permissions prior to taking ownership");
        LogPermissions(directory);
        await TakeOwnershipAsync(directory);

        Logger.Info("Permissions post taking ownership");
        LogPermissions(directory);

        if (!HasWriteAccess(directory))
        {
            throw new UnauthorizedAccessException($"Failed to obtain write access to {directory}.");
        }
    }

    private static async Task TakeOwnershipAsync(string directory)
    {
        await RunElevatedAsync("takeown", $"/f \"{directory}\" /r /a");
        await RunElevatedAsync("icacls", $"\"{directory}\" /grant *S-1-1-0:(F) /T /C");
    }

    private static async Task RunElevatedAsync(string fileName, string arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        using var proc = Process.Start(info);
        if (proc is null)
        {
            throw new InvalidOperationException($"Unable to start process {fileName}");
        }

        await proc.WaitForExitAsync();
    }

    private static void LogPermissions(string directory)
    {
        try
        {
            var di = new DirectoryInfo(directory);
            var acl = di.GetAccessControl(AccessControlSections.Access);
            var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier))
                           .Cast<FileSystemAccessRule>();

            Logger.Info($"ACL for {directory} (DACL only):");
            foreach (var rule in rules)
            {
                var sid = (SecurityIdentifier)rule.IdentityReference;
                var account = sid.Translate(typeof(NTAccount)).Value;
                var rights = rule.FileSystemRights;
                var type = rule.AccessControlType;
                var inherit = rule.InheritanceFlags;
                var prop = rule.PropagationFlags;
                Logger.Info($"  {account}: {rights} {type} (Inherit:{inherit}, Propagate:{prop})");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to read DACL via .NET for {directory}", ex);
        }
    }

    /*------------------------------------------------------------------
     *   FILE OPERATIONS
     *----------------------------------------------------------------*/

    private async Task CreateDimmedFilesAsync(string directory)
    {
        var folders = Directory.EnumerateDirectories(directory)
                               .Where(p => !string.IsNullOrEmpty(p));

        Logger.Info($"CreateDimmedFiles: Lockscreen images found in paths: {string.Join(", ", folders)}");

        var tasks = new List<Task>();
        foreach (var path in folders)
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(path!);

            // Ensure full control on existing main file
            var mainDest = Path.Combine(path!, DimmedBaseName);
            try
            {
                Logger.Info($"Copying main GIF to {mainDest}");
                await CurrentImage!.CopyAsync(folder, DimmedBaseName, NameCollisionOption.ReplaceExisting).AsTask();
            }
            catch (Exception ex)
            {
                try
                {
                    Logger.Error("Failed to copy main LockScreen.jpg, trying to take ownership and trying again", ex);
                    await GrantFullControlOnFileAsync(mainDest);
                    Logger.Info($"Copying main GIF to {mainDest}");
                    await CurrentImage!.CopyAsync(folder, DimmedBaseName, NameCollisionOption.ReplaceExisting).AsTask();
                    Logger.Info("Successfully replaced LockScreen.jpg after taking ownership manually.");
                }
                catch (Exception ex2)
                {
                    Logger.Error("Failed to copy main LockScreen.jpg, static preview may be incorrect when waking up from sleep.", ex2);
                }
            }


            // Copy per-resolution dimmed files
            foreach (var res in DisplayService.GetDisplayResolutions())
            {
                var dest = $"LockScreen___{res}{DimmedSuffix}";
                var destPath = Path.Combine(path!, dest);
                Logger.Info($"Copying GIF to {destPath}");
                try
                {
                    await CurrentImage!.CopyAsync(folder, dest, NameCollisionOption.ReplaceExisting);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to copy dimmed file {destPath}. Trying to take ownership of the file...", ex);
                    await GrantFullControlOnFileAsync(destPath);
                    await CurrentImage!.CopyAsync(folder, dest, NameCollisionOption.ReplaceExisting);
                }
            }
        }
    }

    private static async Task GrantFullControlOnFileAsync(string filePath)
    {
        try
        {
            // Take ownership of the file
            await RunElevatedAsync("takeown", $"/f \"{filePath}\" /a");
            // Grant FullControl to Everyone
            await RunElevatedAsync("icacls", $"\"{filePath}\" /grant:r *S-1-1-0:F /C");
            Logger.Info($"Granted FullControl to Everyone on {filePath}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to grant FullControl on {filePath}: {ex.Message}");
        }
    }

    private static DeleteFilesResult DeleteDimmedFiles(string directory)
    {
        var folders = Directory.EnumerateFiles(directory, DimmedBaseName, SearchOption.AllDirectories)
                               .Select(Path.GetDirectoryName)
                               .Where(p => !string.IsNullOrEmpty(p));

        Logger.Info($"DeleteDimmedFiles: Lockscreen images found in paths: {string.Join(", ", folders)}");

        var result = new DeleteFilesResult();

        foreach (var folder in folders)
        {
            foreach (var file in Directory.GetFiles(folder!, DimmedPattern))
            {
                try
                {
                    File.Delete(file);
                    Logger.Info($"Deleted: {file}");
                    result.SuccessfulDeletions++;
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error deleting file {file}", ex);
                    result.FailedDeletions++;
                }
            }
        }

        return result;
    }

    /*------------------------------------------------------------------
     *   MIME‑TYPE DIAGNOSTICS (content sniffing via UrlMon)
     *----------------------------------------------------------------*/

    [DllImport("urlmon.dll", CharSet = CharSet.Auto)]
    private static extern int FindMimeFromData(
        IntPtr pBC,
        [MarshalAs(UnmanagedType.LPWStr)] string? pwzUrl,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.I1, SizeParamIndex = 3)] byte[]? pBuffer,
        int cbSize,
        [MarshalAs(UnmanagedType.LPWStr)] string? pwzMimeProposed,
        int dwMimeFlags,
        out IntPtr ppwzMimeOut,
        int dwReserved);

    private static string DetectMimeType(string filePath)
    {
        var buffer = new byte[256];
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            var read = fs.Read(buffer, 0, buffer.Length);
            var hr = FindMimeFromData(IntPtr.Zero, null, buffer, read, null, 0, out var mimePtr, 0);
            if (hr != 0 || mimePtr == IntPtr.Zero)
            {
                return "unknown/unknown";
            }

            var mime = Marshal.PtrToStringUni(mimePtr) ?? "unknown/unknown";
            Marshal.FreeCoTaskMem(mimePtr);
            return mime;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to detect MIME type for {filePath}: {ex.Message}");
            return "unknown/unknown";
        }
    }

    private static async Task LogFilesWithMimeTypesAsync(string directory)
    {
        var files = Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            var mime = DetectMimeType(file);
            Logger.Info($"{file} -> {mime}");
        }
        await Task.CompletedTask;
    }
}
