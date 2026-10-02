$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../scripts/Get-LockscreenAccessReport.ps1')

function Assert-Report {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

$testBase = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../out'))
$fixture = Join-Path $testBase ('access-report-tests-' + [guid]::NewGuid().ToString('N'))
$cache = Join-Path $fixture 'synthetic-user/ReadOnly'
$folder = Join-Path $cache 'LockScreen[fixture]'
$image = Join-Path $folder 'LockScreen.jpg'
$originalPathProbe = ${function:Get-LgPathInfo}
$originalRoot = ${function:Get-LgCacheRoot}
try {
    [IO.Directory]::CreateDirectory($folder) | Out-Null
    [IO.File]::WriteAllText($image, 'synthetic cache bytes, never read by the collector')
    foreach ($index in 1..4) {
        [IO.File]::WriteAllText((Join-Path $folder ("LockScreen___${index}_notdimmed.jpg")), 'synthetic variant')
    }
    $beforeAcl = (Get-Acl -LiteralPath $image).Sddl
    $beforeWrite = [IO.File]::GetLastWriteTimeUtc($image)
    $missing = Invoke-LgReadProbe { [IO.File]::GetAttributes((Join-Path $cache 'missing')) }
    Assert-Report ($missing.Status -eq 'Missing') 'Missing paths must not be presented as denied or accessible.'
    $denied = Invoke-LgReadProbe { throw [UnauthorizedAccessException]::new('Synthetic denied metadata.') }
    Assert-Report ($denied.Status -eq 'AccessDenied' -and $denied.Error.HResult -eq '0x80070005') 'Preserve access-denied HRESULT.'
    $nativeDenied = Invoke-LgReadProbe { throw [ComponentModel.Win32Exception]::new(5, 'Synthetic localized message.') }
    Assert-Report ($nativeDenied.Status -eq 'AccessDenied' -and $nativeDenied.Error.NativeErrorCode -eq 5) 'Native access denied must survive a generic Win32Exception HRESULT.'
    Assert-Report ($nativeDenied.Error.HResult -eq '0x80004005') 'Native classification must preserve the original generic HRESULT.'
    foreach ($nativeMissingCode in @(2, 3)) {
        $nativeMissing = Invoke-LgReadProbe { throw [ComponentModel.Win32Exception]::new($nativeMissingCode, 'Synthetic localized message.') }
        Assert-Report ($nativeMissing.Status -eq 'Missing' -and $nativeMissing.Error.NativeErrorCode -eq $nativeMissingCode) 'Native missing-file/path codes must remain distinct from access denial.'
    }
    $nativeOther = Invoke-LgReadProbe { throw [ComponentModel.Win32Exception]::new(32, 'Access is denied') }
    Assert-Report ($nativeOther.Status -eq 'Error' -and $nativeOther.Error.NativeErrorCode -eq 32) 'Do not infer access denied from localized or misleading error text.'
    $unexpected = Invoke-LgReadProbe { throw [InvalidOperationException]::new('Synthetic other failure.') }
    Assert-Report ($unexpected.Status -eq 'Error') 'Unexpected errors must not be described as missing cache.'

    $limited = Get-LgBoundedEntries $folder '*_notdimmed.jpg' 2 -Files
    Assert-Report ($limited.Entries.Count -eq 2 -and $limited.Truncated) 'Bounded inventories must report truncation.'
    $exact = Get-LgBoundedEntries $folder '*_notdimmed.jpg' 4 -Files
    Assert-Report ($exact.Entries.Count -eq 4 -and -not $exact.Truncated) 'Exactly the limit is not truncation.'
    $snapshot = Get-LgCacheSnapshot $cache
    Assert-Report ($snapshot.RootValidation.Status -eq 'Passed') 'Readable ancestor checks should pass.'
    Assert-Report ($snapshot.FolderEnumeration.Status -eq 'Ok' -and $snapshot.Folders.Count -eq 1) 'Inventory must preserve literal brackets in paths.'
    Assert-Report ($snapshot.Folders[0].Files.Count -eq 5) 'Main image and all discovered variants should have metadata.'
    Assert-Report ($snapshot.Folders[0].Files[0].Acl.Status -eq 'Ok') 'ACL evidence should be readable on the synthetic file.'
    Assert-Report ($snapshot.AncestorsLeafFirst[0].Path -eq $cache) 'App-style validation must start at the cache root.'
    $json = $snapshot | ConvertTo-Json -Depth 20
    $roundTrip = $json | ConvertFrom-Json
    Assert-Report ($roundTrip.Folders[0].Files[0].Acl.Value.Sddl.Length -gt 0) 'JSON must retain ACL data.'
    Assert-Report (-not $json.Contains('synthetic cache bytes')) 'Image content must not enter the report.'

    # The exact important relationship: denied ancestor metadata does not imply
    # direct enumeration of the known child path was denied or that it is empty.
    $deniedParent = [IO.Path]::GetDirectoryName($cache)
    function Get-LgPathInfo {
        param([string]$Path)
        if ($Path -eq $deniedParent) {
            return [pscustomobject]@{ Path = $Path; Attributes = $denied; Acl = $denied }
        }
        & $originalPathProbe $Path
    }
    $snapshot = Get-LgCacheSnapshot $cache
    Assert-Report ($snapshot.RootValidation.Status -eq 'Blocked' -and $snapshot.RootValidation.Path -eq $deniedParent) 'Denied parent must block app-style validation.'
    Assert-Report ($snapshot.FolderEnumeration.Status -eq 'Ok' -and $snapshot.Folders.Count -eq 1) 'Independent direct enumeration must still run after parent denial.'

    $descendantProbes = 0
    function Get-LgPathInfo {
        param([string]$Path)
        if ($Path -eq $deniedParent) {
            return [pscustomobject]@{
                Path = $Path
                Attributes = [pscustomobject]@{ Status = 'Ok'; Value = [pscustomobject]@{ IsReparsePoint = $true } }
                Acl = New-LgSkippedProbe 'Synthetic link.'
            }
        }
        if ($Path.StartsWith($deniedParent + '\', [StringComparison]::OrdinalIgnoreCase)) { $script:descendantProbes++ }
        & $originalPathProbe $Path
    }
    $snapshot = Get-LgCacheSnapshot $cache
    Assert-Report ($snapshot.RootValidation.Status -eq 'Blocked' -and $snapshot.FolderEnumeration.Status -eq 'Skipped') 'Visible ancestor links must stop inventory.'
    Assert-Report ($snapshot.RootValidation.Path -eq $deniedParent -and $snapshot.RootValidation.Reason -eq 'ReparsePoint') 'Report the actual visible link, not a skipped descendant.'
    Assert-Report ($descendantProbes -eq 0 -and $snapshot.Folders.Count -eq 0) 'Do not inspect descendants through an observed link.'
    Set-Item Function:Get-LgPathInfo $originalPathProbe

    $empty = Join-Path $fixture 'empty-cache'
    [IO.Directory]::CreateDirectory($empty) | Out-Null
    $snapshot = Get-LgCacheSnapshot $empty
    Assert-Report ($snapshot.FolderEnumeration.Status -eq 'Ok' -and $snapshot.FolderEnumeration.Value.Entries.Count -eq 0) 'Accessible empty cache must be distinct from missing.'
    $snapshot = Get-LgCacheSnapshot (Join-Path $fixture 'absent-cache')
    Assert-Report ($snapshot.RootValidation.Status -eq 'Passed' -and $snapshot.FolderEnumeration.Status -eq 'Missing') 'App validation tolerates missing attributes; enumeration must still expose missing cache.'

    $targetSid = 'S-1-5-21-0-0-0-1000'
    Assert-Report ((Get-LgCacheRoot $targetSid).EndsWith("\$targetSid\ReadOnly")) 'Explicit target SID must select the original user cache.'
    $command = Get-LgComparisonCommand "C:\synthetic's [folder]\report.ps1" $targetSid "C:\synthetic's output"
    $parseTokens = $null
    $parseErrors = $null
    $commandAst = [Management.Automation.Language.Parser]::ParseInput($command, [ref]$parseTokens, [ref]$parseErrors)
    Assert-Report ($parseErrors.Count -eq 0) 'Printed comparison command must be valid PowerShell even with apostrophes.'
    $literals = @($commandAst.FindAll({ param($node) $node -is [Management.Automation.Language.StringConstantExpressionAst] }, $true) | ForEach-Object { $_.Value })
    Assert-Report ($literals -contains "C:\synthetic's [folder]\report.ps1" -and $literals -contains $targetSid) 'Printed command must preserve the script path and original target SID.'
    Assert-Report ((Get-Acl -LiteralPath $image).Sddl -eq $beforeAcl) 'Collection must not change cache permissions.'
    Assert-Report ([IO.File]::GetLastWriteTimeUtc($image) -eq $beforeWrite) 'Collection must not modify cache files.'

    # Exercise complete JSON output without touching the real lock-screen cache.
    # Runtime/token metadata remains private in this temporary, cleaned fixture.
    $smokeCache = Join-Path $fixture 'SystemData/synthetic-user/ReadOnly'
    $reportDirectory = Join-Path $fixture 'reports'
    [IO.Directory]::CreateDirectory($smokeCache) | Out-Null
    [IO.Directory]::CreateDirectory($reportDirectory) | Out-Null
    function Get-LgCacheRoot { param([string]$Sid) $smokeCache }
    Invoke-LgAccessReport $targetSid $reportDirectory (Join-Path $PSScriptRoot '../scripts/Get-LockscreenAccessReport.ps1') 6>$null
    $reportFiles = @([IO.Directory]::GetFiles($reportDirectory, '*.json'))
    Assert-Report ($reportFiles.Count -eq 1) 'Complete collection must write one JSON report.'
    $complete = [IO.File]::ReadAllText($reportFiles[0]) | ConvertFrom-Json
    Assert-Report ($complete.TargetUserSid -eq $targetSid -and $complete.Cache.CacheRoot -eq $smokeCache) 'Complete report must preserve the requested user and probe root.'
    Assert-Report ($complete.Windows.Status -eq 'Ok' -and $complete.Token.Value.ExitCode -eq 0) 'Runtime and token probes must produce valid evidence.'
    Assert-Report ($complete.Cache.FolderEnumeration.Status -eq 'Ok' -and $complete.Cache.Folders.Count -eq 0) 'Empty cache must serialize as an empty inventory.'
    Set-Item Function:Get-LgCacheRoot $originalRoot
    Write-Host 'Lockscreen access report checks passed (synthetic filesystem and denied/link probes).'
} finally {
    Set-Item Function:Get-LgPathInfo $originalPathProbe
    Set-Item Function:Get-LgCacheRoot $originalRoot
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    if (-not $resolvedFixture.StartsWith($testBase + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path.' }
    if (Test-Path -LiteralPath $resolvedFixture) { Remove-Item -LiteralPath $resolvedFixture -Recurse -Force }
}
