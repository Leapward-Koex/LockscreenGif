$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '../LockScreenGif/LockscreenGif.csproj'
$originalGitHubActions = [Environment]::GetEnvironmentVariable('GITHUB_ACTIONS', 'Process')

try {
    # Evaluate the real app project without building, restoring, or sending events.
    # Returning to an unset environment after CI also checks that selection does not stick.
    foreach ($case in @(
        @{ Environment = $null; Configuration = 'Debug'; Production = $false },
        @{ Environment = $null; Configuration = 'Release'; Production = $false },
        @{ Environment = 'false'; Configuration = 'Debug'; Production = $false },
        @{ Environment = 'false'; Configuration = 'Release'; Production = $false },
        @{ Environment = 'true'; Configuration = 'Debug'; Production = $true },
        @{ Environment = 'true'; Configuration = 'Release'; Production = $true },
        @{ Environment = $null; Configuration = 'Release'; Production = $false }
    )) {
        [Environment]::SetEnvironmentVariable('GITHUB_ACTIONS', $case.Environment, 'Process')
        $output = & dotnet msbuild $project -nologo -p:Platform=x64 "-p:Configuration=$($case.Configuration)" -getItem:AssemblyMetadata 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "MSBuild evaluation failed for $($case.Configuration): $($output -join [Environment]::NewLine)"
        }

        $evaluation = ($output -join [Environment]::NewLine) | ConvertFrom-Json
        $markers = @($evaluation.Items.AssemblyMetadata | Where-Object { $_.Identity -eq 'GitHubActionsBuild' })
        $description = "GITHUB_ACTIONS='$($case.Environment)', Configuration=$($case.Configuration)"
        if ($case.Production) {
            if ($markers.Count -ne 1 -or $markers[0].Value -cne 'true') {
                throw "Expected one production build marker for $description."
            }
        } elseif ($markers.Count -ne 0) {
            throw "A non-GitHub build received a production marker for $description."
        }
    }
} finally {
    [Environment]::SetEnvironmentVariable('GITHUB_ACTIONS', $originalGitHubActions, 'Process')
}

Write-Host 'Analytics build selection checks passed (7 MSBuild evaluations; no build or network delivery).'
