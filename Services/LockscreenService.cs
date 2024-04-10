using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LockscreenGif.Contracts.Services;
using System.DirectoryServices.AccountManagement;
using System.IO;
using System.Security.AccessControl;
using System.Diagnostics;
using System.Security.Principal;
using System.Security.Cryptography;
using Windows.Storage;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System.UserProfile;
using Windows.Graphics.Display;

namespace LockscreenGif.Services;
public class LockscreenService : ILockscreenService
{
    private SecurityIdentifier? _currentSid;
    private readonly Task _currentSidTask;

    public StorageFile? CurrentImage { get; set; }
    public BitmapImage? CurrentImageBitmap
    {
        get
        {
            if (CurrentImage == null)
            {
                return null;
            }
            var bitmapImage = new BitmapImage
            {
                UriSource = new Uri(CurrentImage.Path)
            };
            return bitmapImage;
        }
    }

    public LockscreenService()
    {
        _currentSidTask = Task.Run(() =>
        {
            _currentSid = UserPrincipal.Current.Sid;
        });
    }

    private async Task<SecurityIdentifier?> GetCurrentSidAsync()
    {
        await _currentSidTask;
        return _currentSid;
    }


    public async Task<bool> ApplyGifAsLockscreenAsync()
    {
        try
        {
            var sid = await GetCurrentSidAsync();
            Logger.Info($"User SID: {sid}. CurrentImage: {CurrentImage?.Path}");
            if (CurrentImage != null && sid != null)
            {
                Logger.Info($"Calling default Windows API to set lockscreen image");
                await LockScreen.SetImageFileAsync(CurrentImage);
                var lockscreenDirectory = $@"C:\ProgramData\Microsoft\Windows\SystemData\{sid}\ReadOnly";
                Logger.Info($"Trying to take ownership of {lockscreenDirectory}");
                await TakeOwnershipOfLockscreenFolderAsync(lockscreenDirectory);

                await Task.Delay(1000);
                Logger.Info($"Trying to replace dimmed files with file extension spoofed GIF");
                await CreateDimmedFiles(lockscreenDirectory);
                Logger.Info("Successfully set lockscreen");
                return true;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to set lockscreen", ex);
            return false;
        }

        return false;
    }

    private async Task CreateDimmedFiles(string lockscreenDirectory)
    {
        // LockScreen dimmed files with gif file.
        var folderPaths = Directory.EnumerateFiles(lockscreenDirectory, "LockScreen.jpg", SearchOption.AllDirectories).Select(Path.GetDirectoryName);
        Logger.Info($"Lockscreen images in paths {string.Join(", ", folderPaths)}");

        var tasks = folderPaths.Select(async folderPath =>
        {
            if (!string.IsNullOrEmpty(folderPath))
            {
                var resolutionTasks = DisplayService.GetDisplayResolutions().Select(async resolution =>
                {
                    var fileName = $"LockScreen___{resolution}_notdimmed.jpg";
                    Logger.Info($"Copying GIF to {Path.Join(folderPath, fileName)}");
                    try
                    {
                        await CurrentImage!.CopyAsync(await StorageFolder.GetFolderFromPathAsync(folderPath), fileName, NameCollisionOption.ReplaceExisting);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Failed to copy file to {Path.Join(folderPath, fileName)}", ex);
                    }
                });
                await Task.WhenAll(resolutionTasks);
            }
        });
        await Task.WhenAll(tasks);
    }

    private async Task TakeOwnershipOfLockscreenFolderAsync(string lockscreenDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            WindowStyle = ProcessWindowStyle.Hidden,
            FileName = "takeown",
            Arguments = $"/f \"{lockscreenDirectory}\" /r /d y",
            Verb = "runas",
            UseShellExecute = true
        };
        var process = Process.Start(startInfo);
        if (process != null)
        {
            await process.WaitForExitAsync();
        }

        var icalsStartInfo = new ProcessStartInfo
        {
            WindowStyle = ProcessWindowStyle.Hidden,
            FileName = "icacls",
            Arguments = $"\"{lockscreenDirectory}\" /grant Everyone:F /T /C",
            Verb = "runas",
            UseShellExecute = true
        };
        var icaslProcess = Process.Start(icalsStartInfo);
        if (icaslProcess != null)
        {
            await icaslProcess.WaitForExitAsync();
        }

        Logger.Info("Checking permissions post ownership");
        try
        {
            var icalsListPermissionStartInfo = new ProcessStartInfo
            {
                WindowStyle = ProcessWindowStyle.Hidden,
                FileName = "icacls",
                Arguments = $"* /t",
                WorkingDirectory = lockscreenDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var icalsListPermissionProcess = Process.Start(icalsListPermissionStartInfo);
            if (icalsListPermissionProcess != null)
            {
                // Read output and error in separate tasks to avoid deadlocks
                var outputTask = ReadStreamAsync(icalsListPermissionProcess.StandardOutput, Logger.Info);
                var errorTask = ReadStreamAsync(icalsListPermissionProcess.StandardError, Logger.Error);

                await Task.WhenAll(outputTask, errorTask);
                await icalsListPermissionProcess.WaitForExitAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to check permissions", ex);
        }

    }

    private async Task ReadStreamAsync(StreamReader stream, Action<string> logAction)
    {
        string line;
        while ((line = await stream.ReadLineAsync()) != null)
        {
            logAction(line);
        }
    }
}
