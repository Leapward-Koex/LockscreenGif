# Compatibility entrypoint: the permission worker is now compiled C#.
# Its test runner mocks native commands and does not request elevation.
$ErrorActionPreference = 'Stop'
dotnet run --project (Join-Path $PSScriptRoot '../ProcessTracing.Tests/ProcessTracing.Tests.csproj') --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Compiled permission worker regression checks failed.' }
