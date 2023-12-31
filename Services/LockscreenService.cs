﻿using System;
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

    public StorageFile? CurrentImage { get; set;
    }
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
            if (CurrentImage != null && sid != null)
            {
                await LockScreen.SetImageFileAsync(CurrentImage);
                var lockscreenDirectory = $@"C:\ProgramData\Microsoft\Windows\SystemData\{sid}\ReadOnly"; // $@"C:\ProgramData\Microsoft\Windows\SystemData\{sid}\ReadOnly"
                await TakeOwnershipOfLockscreenFolderAsync(lockscreenDirectory);
                await ReplaceLockScreenFileWithGifAsync(lockscreenDirectory);
                await CreateDimmedFiles(lockscreenDirectory);
                return true;

        }
        }
        catch (Exception ex)
        {
            return false;
        }

        return false;
    }

    private async Task ReplaceLockScreenFileWithGifAsync(string lockscreenDirectory)
    {
        // LockScreen.jpg files with gif file.
        var filePaths = Directory.EnumerateFiles(lockscreenDirectory, "LockScreen.jpg", SearchOption.AllDirectories);
        var tasks = filePaths.Select(async filePath => await CurrentImage!.CopyAndReplaceAsync(await StorageFile.GetFileFromPathAsync(filePath)));
        await Task.WhenAll(tasks);
    }

    private async Task CreateDimmedFiles(string lockscreenDirectory)
    {
        // LockScreen dimmed files with gif file.
        var folderPaths = Directory.EnumerateFiles(lockscreenDirectory, "LockScreen.jpg", SearchOption.AllDirectories).Select(Path.GetDirectoryName);

        var tasks = folderPaths.Select(async folderPath =>
        {
            if (!string.IsNullOrEmpty(folderPath))
            {
                var resolutionTasks = DisplayService.GetDisplayResolutions().Select(async resolution =>
                {
                    var fileName = $"LockScreen___{resolution}_notdimmed.jpg";
                    await CurrentImage!.CopyAsync(await StorageFolder.GetFolderFromPathAsync(folderPath), fileName, NameCollisionOption.ReplaceExisting);
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
    }
}
