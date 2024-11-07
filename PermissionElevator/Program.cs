using System.Diagnostics;
using System.DirectoryServices.AccountManagement;
using System.Security.AccessControl;
using System.Security.Principal;

var lockscreenDirectory = $@"C:\ProgramData\Microsoft\Windows\SystemData\{UserPrincipal.Current.Sid}\ReadOnly";

try
{
    var currentUser = WindowsIdentity.GetCurrent();
    if (currentUser.User == null)
    {
        Logger.Error("Unable to get current user to assign ownership to");
        return 1;
    }

    var accessRule = new FileSystemAccessRule(
        currentUser.User,
        FileSystemRights.FullControl,
        AccessControlType.Allow);

    OwnershipHelper.EnablePrivilege("SeTakeOwnershipPrivilege");
    OwnershipHelper.EnablePrivilege("SeRestorePrivilege");
    // Take ownership of the root directory
    var directoryInfo = new DirectoryInfo(lockscreenDirectory);
    var directorySecurity = directoryInfo.GetAccessControl();
    directorySecurity.SetOwner(currentUser.User);
    directorySecurity.AddAccessRule(accessRule);
    directoryInfo.SetAccessControl(directorySecurity);

    // Recursively take ownership of all files and subdirectories
    foreach (var dir in directoryInfo.GetDirectories("*", SearchOption.AllDirectories))
    {
        try
        {
            var dirSecurity = dir.GetAccessControl();
            dirSecurity.SetOwner(currentUser.User);
            dirSecurity.AddAccessRule(accessRule);
            dir.SetAccessControl(dirSecurity);
            Logger.Info($"Took ownership of directory {dir}");
        }
        catch (Exception ex)
        {
            Logger.Error($"Error taking ownership of directory {dir}", ex);
        }

    }

    foreach (var file in directoryInfo.GetFiles("*", SearchOption.AllDirectories))
    {
        try
        {
            var fileSecurity = file.GetAccessControl();
            fileSecurity.SetOwner(currentUser.User);
            fileSecurity.AddAccessRule(accessRule);
            file.SetAccessControl(fileSecurity);
            Logger.Info($"Took ownership of file {file}");
        }
        catch (Exception ex)
        {
            Logger.Error($"Error taking ownership of file {file}", ex);
        }
    }
}
catch (Exception ex)
{
    Logger.Error("Failed to take ownership of lockscreen folder", ex);
}

var icaclsStartInfo = new ProcessStartInfo
{
    WindowStyle = ProcessWindowStyle.Hidden,
    CreateNoWindow = false,
    FileName = "icacls",
    Arguments = $"\"{lockscreenDirectory}\" /grant *S-1-1-0:(F) /T /C",
    RedirectStandardOutput = true,
    RedirectStandardError = true,
};
Logger.Info($"Starting icals of {lockscreenDirectory}");
var icaclsProcess = new Process();
icaclsProcess.OutputDataReceived += (sender, args) => Logger.Info(args.Data);
icaclsProcess.ErrorDataReceived += (sender, args) => Logger.Error(args.Data);
icaclsProcess.StartInfo = icaclsStartInfo;
icaclsProcess.Start();
Logger.Info("Waiting for icals to finish");
icaclsProcess.BeginOutputReadLine();
icaclsProcess.BeginErrorReadLine();
await icaclsProcess.WaitForExitAsync();

Logger.Info("Finished");
return 0;