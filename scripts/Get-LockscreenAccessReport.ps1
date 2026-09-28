#Requires -Version 5.1
<#
.SYNOPSIS
Collects read-only lock-screen cache access evidence for one Windows user.
.DESCRIPTION
Run in normal PowerShell first, then paste the printed comparison command into
an administrator PowerShell window. The explicit target SID keeps both reports
on the original user's cache even when elevation uses another account.
Only the JSON report is written. No ACL, ownership, registry, image or lock state
is changed. Reports contain local paths, SIDs, token details and error messages;
they contain no image bytes and are never uploaded by this script.
#>
[CmdletBinding()]
param(
    [string]$TargetUserSid,
    [string]$OutputDirectory = [Environment]::GetFolderPath('Desktop')
)

function Invoke-LgReadProbe {
    param([scriptblock]$Action)
    try {
        $value = & $Action
        [pscustomobject]@{ Status = 'Ok'; Value = $value; Error = $null }
    } catch {
        $failure = $_.Exception
        while ($null -ne $failure.InnerException) { $failure = $failure.InnerException }
        $code = '0x' + $failure.HResult.ToString('X8')
        # Get-Acl can wrap ERROR_ACCESS_DENIED in Win32Exception with the
        # generic E_FAIL HRESULT. Classify its numeric native code, not text.
        $nativeCode = if ($failure -is [ComponentModel.Win32Exception]) { $failure.NativeErrorCode } else { $null }
        $status = if ($null -ne $nativeCode) {
            switch ($nativeCode) {
                5 { 'AccessDenied' }
                2 { 'Missing' }
                3 { 'Missing' }
                default { 'Error' }
            }
        } else {
            switch ($code) {
                '0x80070005' { 'AccessDenied' }
                '0x80070002' { 'Missing' }
                '0x80070003' { 'Missing' }
                default { 'Error' }
            }
        }
        [pscustomobject]@{
            Status = $status
            Value = $null
            Error = [pscustomobject]@{ Type = $failure.GetType().FullName; HResult = $code; NativeErrorCode = $nativeCode; Message = $failure.Message }
        }
    }
}

function New-LgSkippedProbe {
    param([string]$Reason)
    [pscustomobject]@{ Status = 'Skipped'; Value = $null; Error = $null; Reason = $Reason }
}

function Get-LgAclInfo {
    param([string]$Path)
    $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
    $sections = [Security.AccessControl.AccessControlSections]'Access, Owner, Group'
    [pscustomobject]@{
        OwnerSid = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
        GroupSid = $acl.GetGroup([Security.Principal.SecurityIdentifier]).Value
        InheritanceDisabled = $acl.AreAccessRulesProtected
        Sddl = $acl.GetSecurityDescriptorSddlForm($sections)
        Rules = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | ForEach-Object {
            [pscustomobject]@{
                Sid = $_.IdentityReference.Value
                Type = $_.AccessControlType.ToString()
                Rights = $_.FileSystemRights.ToString()
                RightsMask = [int]$_.FileSystemRights
                Inherited = $_.IsInherited
                InheritanceFlags = $_.InheritanceFlags.ToString()
                PropagationFlags = $_.PropagationFlags.ToString()
            }
        })
    }
}

function Get-LgPathInfo {
    param([string]$Path)
    $attributes = Invoke-LgReadProbe {
        $flags = [IO.File]::GetAttributes($Path)
        [pscustomobject]@{
            Flags = $flags.ToString()
            IsReparsePoint = ($flags -band [IO.FileAttributes]::ReparsePoint) -ne 0
        }
    }
    $acl = if ($attributes.Status -eq 'Ok' -and $attributes.Value.IsReparsePoint) {
        New-LgSkippedProbe 'Visible reparse point: do not follow the link to read its ACL.'
    } else {
        Invoke-LgReadProbe { Get-LgAclInfo $Path }
    }
    [pscustomobject]@{ Path = $Path; Attributes = $attributes; Acl = $acl }
}

function Get-LgBoundedEntries {
    param([string]$Path, [string]$Pattern, [int]$Limit, [switch]$Files)
    $items = [Collections.Generic.List[string]]::new()
    # Keep the enumerable intact: PowerShell otherwise materializes it before
    # the limit is applied. Enumeration failures are caught by the caller probe.
    $enumerable = if ($Files) {
        ,([IO.Directory]::EnumerateFiles($Path, $Pattern, [IO.SearchOption]::TopDirectoryOnly))
    } else {
        ,([IO.Directory]::EnumerateDirectories($Path, $Pattern, [IO.SearchOption]::TopDirectoryOnly))
    }
    $iterator = $enumerable.GetEnumerator()
    $truncated = $false
    try {
        while ($iterator.MoveNext()) {
            if ($items.Count -ge $Limit) { $truncated = $true; break }
            $items.Add($iterator.Current)
        }
    } finally {
        if ($iterator -is [IDisposable]) { $iterator.Dispose() }
    }
    [pscustomobject]@{ Entries = @($items.ToArray()); Truncated = $truncated; Limit = $Limit }
}

function Get-LgCacheSnapshot {
    param([string]$CacheRoot)
    $ancestors = [Collections.Generic.List[string]]::new()
    for ($path = [IO.Path]::GetFullPath($CacheRoot); $null -ne $path; $path = [IO.Path]::GetDirectoryName($path)) {
        $ancestors.Add($path)
    }
    $records = [Collections.Generic.List[object]]::new()
    $link = $null
    # Start at the drive root so a visible ancestor link stops descendant probes.
    for ($index = $ancestors.Count - 1; $index -ge 0; $index--) {
        $path = $ancestors[$index]
        if ($null -ne $link) {
            $skip = New-LgSkippedProbe "Visible ancestor reparse point: $link"
            $record = [pscustomobject]@{ Path = $path; Attributes = $skip; Acl = $skip }
        } else {
            $record = Get-LgPathInfo $path
            if ($record.Attributes.Status -eq 'Ok' -and $record.Attributes.Value.IsReparsePoint) { $link = $path }
        }
        $records.Insert(0, $record)
    }
    $validation = [pscustomobject]@{ Status = 'Passed'; Path = $null; Reason = $null }
    # The app checks leaf -> ancestors and tolerates missing path components.
    foreach ($record in $records) {
        $probe = $record.Attributes
        if ($probe.Status -notin @('Ok', 'Missing')) {
            $validation = [pscustomobject]@{ Status = 'Blocked'; Path = $record.Path; Reason = $probe.Status }
            break
        }
        if ($probe.Status -eq 'Ok' -and $probe.Value.IsReparsePoint) {
            $validation = [pscustomobject]@{ Status = 'Blocked'; Path = $record.Path; Reason = 'ReparsePoint' }
            break
        }
    }
    if ($null -ne $link) {
        $validation = [pscustomobject]@{ Status = 'Blocked'; Path = $link; Reason = 'ReparsePoint' }
    }
    $folders = if ($null -ne $link) {
        New-LgSkippedProbe "Visible reparse point: $link"
    } else {
        # Probe independently even if an ancestor's attributes were denied:
        # direct cache access can differ from the app's ancestor validation.
        Invoke-LgReadProbe { Get-LgBoundedEntries $CacheRoot 'LockScreen*' 32 }
    }
    $folderRecords = [Collections.Generic.List[object]]::new()
    if ($folders.Status -eq 'Ok') {
        foreach ($folder in $folders.Value.Entries) {
            $folderInfo = Get-LgPathInfo $folder
            $variants = if ($folderInfo.Attributes.Status -eq 'Ok' -and $folderInfo.Attributes.Value.IsReparsePoint) {
                New-LgSkippedProbe 'Visible cache-folder reparse point.'
            } else {
                Invoke-LgReadProbe { Get-LgBoundedEntries $folder '*_notdimmed.jpg' 128 -Files }
            }
            $files = [Collections.Generic.List[object]]::new()
            if ($variants.Status -ne 'Skipped') {
                $targets = @([IO.Path]::Combine($folder, 'LockScreen.jpg'))
                if ($variants.Status -eq 'Ok') { $targets += @($variants.Value.Entries) }
                foreach ($target in $targets) { $files.Add((Get-LgPathInfo $target)) }
            }
            $folderRecords.Add([pscustomobject]@{ Folder = $folderInfo; VariantEnumeration = $variants; Files = @($files.ToArray()) })
        }
    }
    [pscustomobject]@{
        CacheRoot = $CacheRoot
        RootValidation = $validation
        AncestorsLeafFirst = @($records.ToArray())
        FolderEnumeration = $folders
        Folders = @($folderRecords.ToArray())
    }
}

function Get-LgCacheRoot {
    param([string]$Sid)
    $canonicalSid = [Security.Principal.SecurityIdentifier]::new($Sid).Value
    [IO.Path]::Combine([Environment]::GetFolderPath('CommonApplicationData'), 'Microsoft', 'Windows', 'SystemData', $canonicalSid, 'ReadOnly')
}

function Get-LgComparisonCommand {
    param([string]$ScriptPath, [string]$Sid, [string]$Destination)
    "powershell.exe -NoProfile -ExecutionPolicy Bypass -File '{0}' -TargetUserSid '{1}' -OutputDirectory '{2}'" -f
        $ScriptPath.Replace("'", "''"), $Sid.Replace("'", "''"), $Destination.Replace("'", "''")
}

function Invoke-LgAccessReport {
    param([string]$Sid, [string]$Destination, [string]$ScriptPath)
    $ErrorActionPreference = 'Stop'
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $currentSid = $identity.User.Value
        if ([string]::IsNullOrWhiteSpace($Sid)) { $Sid = $currentSid }
        $Sid = [Security.Principal.SecurityIdentifier]::new($Sid).Value
        $root = Get-LgCacheRoot $Sid
        $elevated = ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        $outputRoot = [IO.Path]::GetFullPath($Destination)
        if (-not [IO.Directory]::Exists($outputRoot)) { throw 'OutputDirectory must be an existing writable directory.' }
        # Report output must never be placed inside the cache being inspected.
        $systemData = [IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($root))
        if ($outputRoot.TrimEnd('\') -eq $systemData -or $outputRoot.StartsWith($systemData + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Choose an output directory outside SystemData.'
        }
        $report = [ordered]@{
            SchemaVersion = 1
            CapturedAtUtc = [DateTime]::UtcNow.ToString('o')
            TargetUserSid = $Sid
            RunningUserSid = $currentSid
            TargetMatchesRunningUser = $Sid -eq $currentSid
            ElevatedAdministrator = $elevated
            ProcessSessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
            PowerShellVersion = $PSVersionTable.PSVersion.ToString()
            RuntimeVersion = [Environment]::Version.ToString()
            Is64BitProcess = [Environment]::Is64BitProcess
            Windows = Invoke-LgReadProbe {
                $os = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction Stop
                [pscustomobject]@{ ProductName = $os.ProductName; DisplayVersion = $os.DisplayVersion; Build = $os.CurrentBuildNumber; Revision = $os.UBR }
            }
            Token = Invoke-LgReadProbe {
                $details = & ([IO.Path]::Combine([Environment]::SystemDirectory, 'whoami.exe')) /all 2>&1
                [pscustomobject]@{ ExitCode = $LASTEXITCODE; Text = ($details -join [Environment]::NewLine) }
            }
            Cache = Get-LgCacheSnapshot $root
            Limits = @('Metadata only; no image contents or write-access tests.', 'No recursive inventory; at most 32 cache folders and 128 variants per folder.', 'Probes are sequential, not an atomic snapshot.', 'PowerShell runtime may differ from the app runtime; this report does not prove playback or helper repair success.')
        }
        $mode = if ($elevated) { 'admin' } else { 'normal' }
        $name = 'LockscreenAccess-{0}-{1}-{2}.json' -f $mode, [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N').Substring(0, 8)
        $outputPath = [IO.Path]::Combine($outputRoot, $name)
        [IO.File]::WriteAllText($outputPath, ($report | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
        Write-Host "Saved: $outputPath"
        Write-Host "Elevated administrator: $elevated; target matches running user: $($Sid -eq $currentSid)"
        Write-Host "App-style root validation: $($report.Cache.RootValidation.Status) $($report.Cache.RootValidation.Reason) $($report.Cache.RootValidation.Path)"
        Write-Host "Direct cache enumeration: $($report.Cache.FolderEnumeration.Status)"
        Write-Host 'The report contains local paths, account SIDs and token details. No image data was read or uploaded.'
        if (-not $elevated) {
            Write-Host 'Next, paste this command into an administrator PowerShell window; it keeps the same target user:'
            Write-Host (Get-LgComparisonCommand $ScriptPath $Sid $outputRoot)
        } else {
            Write-Host 'Compare with the normal report for the same TargetUserSid. Administrator access alone does not prove the unelevated app can access the cache.'
        }
    } finally {
        $identity.Dispose()
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-LgAccessReport -Sid $TargetUserSid -Destination $OutputDirectory -ScriptPath $PSCommandPath
}
