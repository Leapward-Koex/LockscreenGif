using System;
using System.IO;

namespace LockscreenGif.Services;

public static class TempDirectoryService
{
    public static string GetAppTempRoot()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LockscreenGif",
            "Temp");

        Directory.CreateDirectory(root);
        return root;
    }
}
