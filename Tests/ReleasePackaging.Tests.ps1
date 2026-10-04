#requires -Version 7.0
param(
    [string]$AppDirectory = "$PSScriptRoot/../artifacts/app",
    [string]$InstallerProject = "$PSScriptRoot/../Installer/LockscreenGif.aip"
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$appRoot = (Resolve-Path -LiteralPath $AppDirectory).Path
& "$repo/Tests/NativeRuntimePackaging.Tests.ps1"
& "$repo/scripts/Test-NativeRuntimePackaging.ps1" -AppDirectory $appRoot -InstallerProject $InstallerProject
[xml]$installer = Get-Content -LiteralPath $InstallerProject -Raw
$files = $installer.SelectNodes("//COMPONENT[@cid='caphyon.advinst.msicomp.MsiFilesComponent']/ROW")
$payload = @()
$components = @{}
$directories = @{}
foreach ($row in $installer.SelectNodes("//COMPONENT[@cid='caphyon.advinst.msicomp.MsiCompsComponent']/ROW")) { $components[$row.Component] = $row }
foreach ($row in $installer.SelectNodes("//COMPONENT[@cid='caphyon.advinst.msicomp.MsiDirsComponent']/ROW")) { $directories[$row.Directory] = $row }
foreach ($row in $files) {
    $source = if ([IO.Path]::IsPathFullyQualified($row.SourcePath)) {
        [IO.Path]::GetFullPath($row.SourcePath)
    } else {
        [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent ([IO.Path]::GetFullPath($InstallerProject))) $row.SourcePath))
    }
    if (-not $source.StartsWith(([IO.Path]::GetFullPath($appRoot) + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Installer input is outside the publish directory: $($row.File)"
    }
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing installer payload: $($row.File)" }
    $payload += $source
    # Source presence alone cannot catch a file installed into the wrong folder.
    $installedPath = $row.FileName.Split('|')[-1]
    $directory = $components[$row.Component_].Directory_
    $visited = @()
    while ($directory -ne 'APPDIR') {
        if (-not $directories.ContainsKey($directory) -or $directory -in $visited) { throw "Invalid MSI directory for $($row.File)" }
        $visited += $directory
        $entry = $directories[$directory]
        $installedPath = Join-Path ($entry.DefaultDir.Split(':')[0].Split('|')[-1]) $installedPath
        $directory = $entry.Directory_Parent
    }
    $relative = [IO.Path]::GetRelativePath($appRoot, $source)
    if ($installedPath -ine $relative) { throw "MSI installs $relative at the wrong path: $installedPath" }
}
foreach ($file in Get-ChildItem -LiteralPath $appRoot -Recurse -File | Where-Object Extension -ne '.pdb') {
    if ($file.FullName -notin $payload) { throw "Published file is missing from the MSI: $($file.FullName)" }
}
foreach ($relative in @('LockscreenGif.runtimeconfig.json', 'Helpers/Privileged/LockscreenGif.Privileged.Helper.runtimeconfig.json')) {
    $runtime = Get-Content -LiteralPath (Join-Path $appRoot $relative) -Raw | ConvertFrom-Json
    if ($runtime.runtimeOptions.tfm -ne 'net10.0') { throw "Wrong runtime target in $relative" }
    $frameworks = @($runtime.runtimeOptions.framework) + @($runtime.runtimeOptions.frameworks)
    foreach ($framework in $frameworks | Where-Object { $null -ne $_ }) {
        if ($framework.version -notlike '10.0.*') { throw "Unexpected framework in $relative" }
    }
}
$condition = $installer.SelectSingleNode("//ROW[contains(@Condition, 'LOCKSCREENGIF_WINDOWS_BUILD')]")
if (-not $condition -or $condition.Condition -notmatch 'Installed OR' -or $condition.Condition -notmatch '>= 26100') {
    throw 'MSI must enforce Windows build 26100 without blocking maintenance/uninstall.'
}
$search = $installer.SelectSingleNode("//ROW[@Signature_='LockscreenGifWindowsBuild'][@Key]")
if (-not $search -or $search.Name -ne 'CurrentBuildNumber' -or $search.Type -ne '18') { throw 'Missing native 64-bit OS build registry lookup.' }
Write-Host "PASS: $($files.Count) MSI inputs exist in the publish directory, app/helper use .NET 10, and Windows minimum matches the installer."
