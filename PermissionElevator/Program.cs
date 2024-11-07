using System.Diagnostics;
using System.DirectoryServices.AccountManagement;

var lockscreenDirectory = $@"C:\ProgramData\Microsoft\Windows\SystemData\{UserPrincipal.Current.Sid}\ReadOnly";

var startInfo = new ProcessStartInfo
{
    WindowStyle = ProcessWindowStyle.Normal,
    CreateNoWindow = false,
    FileName = "takeown",
    Arguments = $"/f \"{lockscreenDirectory}\" /r /d y",
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
};
Logger.Info($"Starting takeown of {lockscreenDirectory}");
var process = new Process();
process.OutputDataReceived += (sender, args) => Logger.Info(args.Data);
process.ErrorDataReceived += (sender, args) => Logger.Error(args.Data);
process.StartInfo = startInfo;
process.Start();
Logger.Info("Waiting for takeown to finish");
process.BeginOutputReadLine();
process.BeginErrorReadLine();
await process.WaitForExitAsync();

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