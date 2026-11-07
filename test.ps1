# test.ps1
# Banc de tests et validation pour NexusFree
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

Push-Location $PSScriptRoot
try {
    # Every PowerShell script of the package parses (install.ps1, get.ps1, daemon\setup-system.ps1).
    foreach ($script in Get-ChildItem -Path . -Filter *.ps1 -Recurse -File | Where-Object FullName -notmatch '[\\/](bin|obj|TestResults)[\\/]') {
        $errors = $null
        [void][System.Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$null, [ref]$errors)
        if ($errors) { throw "$($script.FullName): $($errors -join '; ')" }
    }

    dotnet test --project tests/NexusFree.Tests/NexusFree.Tests.csproj
    if ($LASTEXITCODE) { throw 'Tests failed.' }
} finally { Pop-Location }

Write-Host 'NexusFree: all checks passed.'
