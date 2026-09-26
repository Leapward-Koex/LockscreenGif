param(
    [Parameter(Mandatory)][ValidateRange(1, 16776959)][long]$RunNumber,
    [Parameter(Mandatory)][ValidateRange(1, 2147483647)][int]$RunAttempt,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$Commit,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
# MSI limits major/minor to 255 and build to 65535. Stay below 65535
# for .NET assembly components too. Epoch 3 sorts after existing 2.1.N installers.
$minor = [long][Math]::Floor($RunNumber / 65535)
$patch = $RunNumber % 65535
$version = "3.$minor.$patch"
$identity = [ordered]@{
    version = $version
    informational_version = "$version-ci+$($Commit.ToLowerInvariant()).attempt.$RunAttempt"
    tag = "build-$RunNumber"
}
if ($OutputPath) {
    $identity.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" } |
        Add-Content -LiteralPath $OutputPath -Encoding utf8
}
[pscustomobject]$identity
