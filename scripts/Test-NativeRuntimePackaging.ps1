#requires -Version 7.0
param(
    [string]$AppDirectory = "$PSScriptRoot/../artifacts/app",
    [string]$InstallerProject = "$PSScriptRoot/../Installer/LockscreenGif.aip"
)
$ErrorActionPreference = 'Stop'
$appRoot = (Resolve-Path -LiteralPath $AppDirectory).Path
[xml]$installer = Get-Content -LiteralPath $InstallerProject -Raw
$files = $installer.SelectNodes("//COMPONENT[@cid='caphyon.advinst.msicomp.MsiFilesComponent']/ROW")
$components = $installer.SelectNodes("//COMPONENT[@cid='caphyon.advinst.msicomp.MsiCompsComponent']/ROW")
$features = $installer.SelectNodes("//COMPONENT[@cid='caphyon.advinst.msicomp.MsiFeatCompsComponent']/ROW")
$directories = @{}
foreach ($row in $installer.SelectNodes("//COMPONENT[@cid='caphyon.advinst.msicomp.MsiDirsComponent']/ROW")) {
    $directories[$row.Directory] = $row
}

foreach ($relative in @('Vendor\gifski\gifski.dll', 'Vendor\gifski\vcruntime140.dll')) {
    $path = Join-Path $appRoot $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing native payload: $relative" }
    $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($path))
    try {
        $valid = $false
        if ($reader.BaseStream.Length -ge 64 -and $reader.ReadUInt16() -eq 0x5A4D) {
            $reader.BaseStream.Position = 0x3C
            $peOffset = $reader.ReadInt32()
            if ($peOffset -ge 64 -and $peOffset -le $reader.BaseStream.Length - 26) {
                $reader.BaseStream.Position = $peOffset
                $signature = $reader.ReadUInt32()
                $machine = $reader.ReadUInt16()
                $reader.BaseStream.Position = $peOffset + 20
                $optionalHeaderSize = $reader.ReadUInt16()
                $characteristics = $reader.ReadUInt16()
                $magic = $reader.ReadUInt16()
                $valid = $signature -eq 0x00004550 -and $machine -eq 0x8664 -and $magic -eq 0x020B -and
                    ($characteristics -band 0x2000) -ne 0 -and $optionalHeaderSize -ge 2 -and
                    $peOffset + 24 + $optionalHeaderSize -le $reader.BaseStream.Length
            }
        }
        if (-not $valid) { throw "Native payload must be an x64 PE32+ DLL: $relative" }
    } finally {
        $reader.Dispose()
    }

    $rows = @($files | Where-Object { $_.SourcePath.EndsWith('\' + $relative, [StringComparison]::OrdinalIgnoreCase) })
    if ($rows.Count -ne 1) { throw "MSI must contain exactly one native payload entry: $relative" }
    $component = @($components | Where-Object Component -eq $rows[0].Component_)
    if ($component.Count -ne 1 -or ([int]$component[0].Attributes -band 256) -eq 0) {
        throw "Native payload needs an x64 MSI component: $relative"
    }
    if (@($features | Where-Object { $_.Component_ -eq $component[0].Component -and $_.Feature_ -eq 'MainFeature' }).Count -ne 1) {
        throw "Native payload must belong to the MSI MainFeature: $relative"
    }
    $installedPath = $rows[0].FileName.Split('|')[-1]
    $directory = $component[0].Directory_
    $visited = @()
    while ($directory -ne 'APPDIR') {
        if (-not $directories.ContainsKey($directory) -or $directory -in $visited) {
            throw "Invalid MSI native payload directory: $relative"
        }
        $visited += $directory
        $entry = $directories[$directory]
        $installedPath = Join-Path ($entry.DefaultDir.Split(':')[0].Split('|')[-1]) $installedPath
        $directory = $entry.Directory_Parent
    }
    if ($installedPath -ine $relative) { throw "MSI installs native payload at the wrong path: $relative -> $installedPath" }
}
$provenancePath = Join-Path $appRoot 'Vendor/gifski/runtime-provenance.json'
if (-not (Test-Path -LiteralPath $provenancePath -PathType Leaf)) { throw 'Missing runtime provenance.' }
$provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
$runtime = Get-Item -LiteralPath (Join-Path $appRoot 'Vendor/gifski/vcruntime140.dll')
if ($provenance.file -ne 'vcruntime140.dll' -or $provenance.architecture -ne 'x64' -or $provenance.peMachine -ne '0x8664' -or
    $provenance.modified -ne $false -or $provenance.bytes -ne $runtime.Length -or
    $provenance.fileVersion -ne $runtime.VersionInfo.FileVersion -or
    $provenance.sha256 -notmatch '^[0-9A-Fa-f]{64}$' -or $provenance.sha256 -ne (Get-FileHash -LiteralPath $runtime.FullName -Algorithm SHA256).Hash) {
    throw 'App-local runtime does not match its redistribution provenance.'
}
$runtimeRow = @($files | Where-Object { $_.SourcePath.EndsWith('\Vendor\gifski\vcruntime140.dll', [StringComparison]::OrdinalIgnoreCase) })
if ($runtimeRow[0].Version -ne $provenance.fileVersion) { throw 'MSI runtime version must match the redistributed native file.' }
$noticePath = Join-Path $appRoot 'Vendor/gifski/runtime-README.txt'
if ($provenance.notice -ne 'runtime-README.txt' -or -not (Test-Path -LiteralPath $noticePath -PathType Leaf) -or
    (Get-Item -LiteralPath $noticePath).Length -eq 0) { throw 'Missing runtime redistribution notice.' }
Write-Host 'PASS: x64 Gifski and app-local VC runtime are installed together; runtime hash/version match the included provenance and notice.'
