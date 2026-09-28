[CmdletBinding()]
param([switch]$Elevate)

$ErrorActionPreference = 'Stop'
$taskProject = Join-Path $PSScriptRoot 'ProcessTracing.Tests/ProcessTracing.Tests.csproj'
dotnet build $taskProject -c Release
if ($LASTEXITCODE -ne 0) { throw 'Native permission fixture build failed.' }
$taskBinary = Join-Path $PSScriptRoot 'ProcessTracing.Tests/bin/Release/net10.0-windows/win-x64/ProcessTracing.Tests.exe'
$taskReport = Join-Path (Split-Path -Parent $taskBinary) ('native-permissions-' + [guid]::NewGuid().ToString('N') + '.txt')
if ($Elevate) {
    # Only the test host is elevated; it creates and restores private temporary
    # fixtures. It does not inspect or alter the real Windows lock-screen cache.
    $taskProcess = Start-Process -FilePath $taskBinary -ArgumentList @('--native-permissions', ('"' + $taskReport + '"')) -Verb RunAs -WindowStyle Hidden -Wait -PassThru
    $taskExit = $taskProcess.ExitCode
} else {
    & $taskBinary --native-permissions $taskReport
    $taskExit = $LASTEXITCODE
}
Get-Content -LiteralPath $taskReport
if ($taskExit -ne 0) { throw "Native permission fixture failed; details: $taskReport" }
