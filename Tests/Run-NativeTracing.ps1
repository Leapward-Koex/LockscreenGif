param(
    [ValidateSet('Collector','Transport')][string]$Mode = 'Collector',
    [string]$HelperDirectory = "$PSScriptRoot/../artifacts/app/Helpers/Privileged"
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'ProcessTracing.Tests/ProcessTracing.Tests.csproj'
dotnet build $project -c Release --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Test harness build failed' }
$output = Join-Path $PSScriptRoot 'ProcessTracing.Tests/bin/Release/net10.0-windows/win-x64'
$report = Join-Path $PSScriptRoot "ProcessTracing.Tests/bin/native-$Mode-result.txt"
if ($Mode -eq 'Collector') {
    $process = Start-Process -FilePath (Join-Path $output 'ProcessTracing.Tests.exe') -ArgumentList @('--native', ('"' + $report + '"')) -Verb RunAs -WindowStyle Hidden -PassThru
} else {
    $staging = Join-Path $PSScriptRoot 'ProcessTracing.Tests/bin/transport-host'
    $privateDirectory = Join-Path $staging 'Helpers/Privileged'
    New-Item -ItemType Directory -Path $privateDirectory -Force | Out-Null
    Get-ChildItem -LiteralPath $output -File | Copy-Item -Destination $staging -Force
    Copy-Item -LiteralPath (Join-Path $output 'ProcessTracing.Tests.exe') -Destination (Join-Path $staging 'LockscreenGif.exe') -Force
    Get-ChildItem -LiteralPath $HelperDirectory | Copy-Item -Destination $privateDirectory -Recurse -Force
    # Leave this host unelevated; its production client requests elevation exactly once.
    $process = Start-Process -FilePath (Join-Path $staging 'LockscreenGif.exe') -ArgumentList @('--transport', ('"' + $report + '"')) -WindowStyle Hidden -PassThru
}
$process.WaitForExit()
if ($process.ExitCode -ne 0) { Get-Content -LiteralPath $report; throw "Native $Mode test failed" }
Write-Output "PASS: native $Mode fixture. Verification evidence: $report"
