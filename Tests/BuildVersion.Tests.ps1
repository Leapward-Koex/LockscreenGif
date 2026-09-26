$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot '../scripts/Get-BuildVersion.ps1'
$sha = '0123456789abcdef0123456789abcdef01234567'
foreach ($case in @(
    @{ Run = 1; Expected = '3.0.1' },
    @{ Run = 65534; Expected = '3.0.65534' },
    @{ Run = 65535; Expected = '3.1.0' },
    @{ Run = 16776959; Expected = '3.255.65534' }
)) {
    $first = & $script -RunNumber $case.Run -RunAttempt 1 -Commit $sha
    $rerun = & $script -RunNumber $case.Run -RunAttempt 2 -Commit $sha
    if ($first.version -ne $case.Expected) { throw "Wrong version for $($case.Run)" }
    if ($first.version -ne $rerun.version -or $first.tag -ne $rerun.tag) { throw 'Rerun changed package identity' }
    if ($first.informational_version -eq $rerun.informational_version) { throw 'Attempt missing from identity' }
    if ($first.informational_version -ne "$($case.Expected)-ci+$sha.attempt.1") { throw 'Incorrect source identity' }
}
foreach ($invalid in @(0, -1, 16776960)) {
    $rejected = $false
    try { & $script -RunNumber $invalid -RunAttempt 1 -Commit $sha | Out-Null }
    catch { $rejected = $true }
    if (!$rejected) { throw "Accepted out-of-range counter $invalid" }
}
$output = Join-Path ([IO.Path]::GetTempPath()) ("build-version-" + [guid]::NewGuid() + '.txt')
try {
    & $script -RunNumber 42 -RunAttempt 3 -Commit $sha -OutputPath $output | Out-Null
    $lines = Get-Content $output
    if ($lines.Count -ne 3 -or $lines[0] -ne 'version=3.0.42' -or $lines[2] -ne 'tag=build-42') { throw 'Incorrect GitHub outputs' }
} finally {
    if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output }
}
Write-Host 'Build version checks passed.'
