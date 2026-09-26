[CmdletBinding()]
param(
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$savedPlatform = $env:Platform
Push-Location $repositoryRoot
try {
    # The solution does not include the standalone test harnesses.
    $targets = @('LockscreenGif.sln') + @(
        Get-ChildItem -LiteralPath Tests -Recurse -Filter *.csproj |
            Sort-Object FullName |
            ForEach-Object { $_.FullName }
    )

    foreach ($target in $targets) {
        # dotnet format has no -p option; MSBuild reads Platform from the environment.
        # The WinUI app cannot load as AnyCPU. Leave test-project defaults intact.
        $env:Platform = if ($target -eq 'LockscreenGif.sln') { 'x64' } else { $savedPlatform }
        $action = if ($Check) { 'Checking' } else { 'Fixing' }
        Write-Host "$action required braces: $target"
        $formatArguments = @('format', 'style', $target, '--diagnostics', 'IDE0011')
        if ($Check) {
            $formatArguments += '--verify-no-changes'
        }
        & dotnet @formatArguments
        if ($LASTEXITCODE -ne 0) {
            throw "Required-brace check or fix failed: $target"
        }
    }

    # Run last: the brace fixer can introduce whitespace that needs reformatting.
    $command = if ($Check) { 'check' } else { 'format' }
    & dotnet csharpier $command .
    if ($LASTEXITCODE -ne 0) {
        throw "CSharpier $command failed. Run dotnet tool restore if the tool is unavailable."
    }
}
finally {
    $env:Platform = $savedPlatform
    Pop-Location
}
