param(
    [string]$AppDirectory = "$PSScriptRoot/../LockScreenGif/bin/Release/net9.0-windows10.0.26100.0/win-x64",
    [string]$InstallerProject = "$PSScriptRoot/../Installer/LockscreenGif.aip"
)
$ErrorActionPreference = 'Stop'
$appRoot = (Resolve-Path -LiteralPath $AppDirectory).Path
$helperRoot = Join-Path $appRoot 'Helpers/Privileged'
[xml]$installer = Get-Content -LiteralPath $InstallerProject -Raw
$files = $installer.SelectNodes("//COMPONENT[@cid='caphyon.advinst.msicomp.MsiFilesComponent']/ROW")
$components = $installer.SelectNodes("//COMPONENT[@cid='caphyon.advinst.msicomp.MsiCompsComponent']/ROW")
$features = $installer.SelectNodes("//COMPONENT[@cid='caphyon.advinst.msicomp.MsiFeatCompsComponent']/ROW")
$expected = Get-ChildItem -LiteralPath $helperRoot -Recurse -File | Where-Object Extension -ne '.pdb'
if ($expected.Count -lt 10) { throw 'Helper dependencies are missing' }
foreach ($file in $expected) {
    $relative = [IO.Path]::GetRelativePath($appRoot, $file.FullName).Replace('/', '\')
    $row = @($files | Where-Object { $_.SourcePath.EndsWith('\' + $relative, [StringComparison]::OrdinalIgnoreCase) })
    if ($row.Count -ne 1) { throw "MSI inventory must contain exactly one entry for $relative" }
    if (-not ($components | Where-Object Component -eq $row[0].Component_)) { throw "Missing component for $relative" }
    if (-not ($features | Where-Object Component_ -eq $row[0].Component_)) { throw "Missing feature for $relative" }
}
if (-not (Test-Path -LiteralPath (Join-Path $appRoot 'LockscreenGif.Privileged.Contracts.dll'))) {
    throw 'App contracts assembly is missing'
}
$deps = Get-Content -LiteralPath (Join-Path $helperRoot 'LockscreenGif.Privileged.Helper.deps.json') -Raw | ConvertFrom-Json
if (-not $deps.libraries.'Microsoft.Diagnostics.Tracing.TraceEvent/3.2.6') { throw 'Unexpected TraceEvent dependency version' }
$process = Start-Process -FilePath (Join-Path $helperRoot 'LockscreenGif.Privileged.Helper.exe') -PassThru -WindowStyle Hidden
$process.WaitForExit()
if ($process.ExitCode -ne 2) { throw 'Packaged helper did not run its argument validation successfully' }
Write-Output "PASS: $($expected.Count) private helper files, pinned TraceEvent, MSI inventory and packaged executable startup."
