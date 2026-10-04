#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$check = Join-Path $repo 'scripts/Test-NativeRuntimePackaging.ps1'
$sourceRoot = Join-Path $repo 'LockScreenGif/Vendor/gifski'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('native-runtime-packaging-' + [guid]::NewGuid())))
$appRoot = Join-Path $fixtureRoot 'app'
$nativeRoot = Join-Path $appRoot 'Vendor/gifski'
$installerPath = Join-Path $fixtureRoot 'fixture.aip'
$runtimePath = Join-Path $nativeRoot 'vcruntime140.dll'
$fixtureXml = @'
<DOCUMENT>
  <COMPONENT cid="caphyon.advinst.msicomp.MsiDirsComponent">
    <ROW Directory="Vendor_Dir" Directory_Parent="APPDIR" DefaultDir="Vendor" />
    <ROW Directory="gifski_Dir" Directory_Parent="Vendor_Dir" DefaultDir="gifski" />
  </COMPONENT>
  <COMPONENT cid="caphyon.advinst.msicomp.MsiCompsComponent">
    <ROW Component="gifski.dll" Directory_="gifski_Dir" Attributes="256" />
    <ROW Component="vcruntime140.dll" Directory_="gifski_Dir" Attributes="256" />
  </COMPONENT>
  <COMPONENT cid="caphyon.advinst.msicomp.MsiFilesComponent">
    <ROW File="gifski.dll" Component_="gifski.dll" FileName="gifski.dll" SourcePath="..\artifacts\app\Vendor\gifski\gifski.dll" />
    <ROW File="vcruntime140.dll" Component_="vcruntime140.dll" FileName="vcruntime140.dll" Version="__RUNTIME_VERSION__" SourcePath="..\artifacts\app\Vendor\gifski\vcruntime140.dll" />
  </COMPONENT>
  <COMPONENT cid="caphyon.advinst.msicomp.MsiFeatCompsComponent">
    <ROW Feature_="MainFeature" Component_="gifski.dll" />
    <ROW Feature_="MainFeature" Component_="vcruntime140.dll" />
  </COMPONENT>
</DOCUMENT>
'@
$fixtureXml = $fixtureXml.Replace('__RUNTIME_VERSION__', (Get-Item -LiteralPath (Join-Path $sourceRoot 'vcruntime140.dll')).VersionInfo.FileVersion)
function Assert-Rejected([string]$ExpectedMessage) {
    $rejected = $false
    try { & $check -AppDirectory $appRoot -InstallerProject $installerPath | Out-Null }
    catch {
        if ($_.Exception.Message -notlike "*$ExpectedMessage*") { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw "Native packaging validation accepted: $ExpectedMessage" }
}
try {
    New-Item -ItemType Directory -Path $nativeRoot -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'gifski.dll') -Destination $nativeRoot
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'vcruntime140.dll') -Destination $nativeRoot
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'runtime-provenance.json'), (Join-Path $sourceRoot 'runtime-README.txt') -Destination $nativeRoot
    [xml]$installer = $fixtureXml
    $installer.Save($installerPath)
    & $check -AppDirectory $appRoot -InstallerProject $installerPath

    foreach ($case in @('MissingRuntime', 'WrongArchitecture', 'MissingMsiEntry', 'WrongMsiDirectory', 'MissingFeature', 'WrongComponentArchitecture', 'ChangedRuntime', 'MissingProvenance', 'MissingNotice', 'WrongMsiVersion')) {
        Copy-Item -LiteralPath (Join-Path $sourceRoot 'vcruntime140.dll') -Destination $runtimePath -Force
        Copy-Item -LiteralPath (Join-Path $sourceRoot 'runtime-provenance.json'), (Join-Path $sourceRoot 'runtime-README.txt') -Destination $nativeRoot -Force
        [xml]$installer = $fixtureXml
        switch ($case) {
            'MissingRuntime' {
                Remove-Item -LiteralPath $runtimePath
                $expected = 'Missing native payload'
            }
            'WrongArchitecture' {
                $stream = [IO.File]::Open($runtimePath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite)
                try {
                    $reader = [IO.BinaryReader]::new($stream, [Text.Encoding]::UTF8, $true)
                    $stream.Position = 0x3C
                    $peOffset = $reader.ReadInt32()
                    $reader.Dispose()
                    $stream.Position = $peOffset + 4
                    $stream.Write([BitConverter]::GetBytes([uint16]0x014C))
                } finally { $stream.Dispose() }
                $expected = 'x64 PE32+ DLL'
            }
            'MissingMsiEntry' {
                $row = $installer.SelectSingleNode("//ROW[@File='vcruntime140.dll']")
                $row.ParentNode.RemoveChild($row) | Out-Null
                $expected = 'exactly one native payload entry'
            }
            'WrongMsiDirectory' {
                $installer.SelectSingleNode("//ROW[@Component='vcruntime140.dll']").Directory_ = 'Vendor_Dir'
                $expected = 'wrong path'
            }
            'MissingFeature' {
                $row = $installer.SelectSingleNode("//ROW[@Feature_='MainFeature'][@Component_='vcruntime140.dll']")
                $row.ParentNode.RemoveChild($row) | Out-Null
                $expected = 'MSI MainFeature'
            }
            'WrongComponentArchitecture' {
                $installer.SelectSingleNode("//ROW[@Component='vcruntime140.dll']").Attributes = '0'
                $expected = 'x64 MSI component'
            }
            'ChangedRuntime' {
                $stream = [IO.File]::Open($runtimePath, [IO.FileMode]::Append, [IO.FileAccess]::Write)
                try { $stream.WriteByte(0) } finally { $stream.Dispose() }
                $expected = 'redistribution provenance'
            }
            'MissingProvenance' {
                Remove-Item -LiteralPath (Join-Path $nativeRoot 'runtime-provenance.json')
                $expected = 'Missing runtime provenance'
            }
            'MissingNotice' {
                Remove-Item -LiteralPath (Join-Path $nativeRoot 'runtime-README.txt')
                $expected = 'Missing runtime redistribution notice'
            }
            'WrongMsiVersion' {
                $installer.SelectSingleNode("//ROW[@File='vcruntime140.dll']").Version = '14.0.0.0'
                $expected = 'MSI runtime version must match'
            }
        }
        $installer.Save($installerPath)
        Assert-Rejected $expected
    }
} finally {
    if (-not $fixtureRoot.StartsWith(($tempRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing cleanup outside the temporary directory.'
    }
    if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
}
Write-Host 'PASS: native packaging rejects missing, changed, wrong-architecture, misrouted and unselected runtime payloads and absent provenance/notice.'
