# test.ps1
# Banc de tests et validation pour NexusFree
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

Push-Location $PSScriptRoot
try {
    dotnet test --project tests/NexusFree.Tests/NexusFree.Tests.csproj
    if ($LASTEXITCODE) { throw 'Tests failed.' }
} finally { Pop-Location }

Write-Host 'NexusFree: all checks passed.'
