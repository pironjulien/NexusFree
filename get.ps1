# NexusFree for Windows 10/11 (Windows PowerShell 5.1 or PowerShell 7), nothing else to install (the engine is one
# self-contained executable). Run it as the account that will use NexusFree, as a file, never from memory: download
# get.ps1 and SHA256SUMS.txt from the latest release, compare get.ps1's SHA-256 with its line in SHA256SUMS.txt
# (provenance: `gh attestation verify get.ps1 --repo pironjulien/NexusFree`), then
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\get.ps1
#
# It downloads the latest release's NexusFree-win-x64.zip or NexusFree-win-arm64.zip (this PC's architecture), checks
# its SHA-256 against the release's SHA256SUMS.txt before extracting anything, unpacks it into
# %USERPROFILE%\.nexus\nexus-free\releases\<version>, then runs its install.ps1 -Apply: one UAC prompt the first
# time, none for an update. Read SECURITY.md first: this account will be able to act as SYSTEM without a prompt.
# NEXUS_RELEASE_TAG: install that release (vX.Y.Z, or a development build vX.Y.Z-dev.N) instead of the latest.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
# Windows PowerShell started from PowerShell 7 inherits its module folders, whose modules it cannot load (Get-FileHash,
# Unblock-File...): keep only its own.
if ($PSVersionTable.PSEdition -ne 'Core') {
    $env:PSModulePath = (($env:PSModulePath -split ';') | Where-Object { $_ -and $_ -notmatch '\\PowerShell\\(7|Modules)' }) -join ';'
}
$ProgressPreference = 'SilentlyContinue'  # Invoke-WebRequest is many times slower with its progress bar
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$Repository = 'pironjulien/NexusFree'

function Get-Architecture {
    $native = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
    if ($native -eq 'ARM64') { return 'win-arm64' }
    if ($native -eq 'AMD64') { return 'win-x64' }
    throw "Unsupported Windows architecture ($native): NexusFree ships for x64 and Arm64."
}

function Get-LatestTag {
    if ($env:NEXUS_RELEASE_TAG) {
        if ($env:NEXUS_RELEASE_TAG -notmatch '^v\d+\.\d+\.\d+(-dev\.\d+)?$') { throw "NEXUS_RELEASE_TAG is not a release tag: $env:NEXUS_RELEASE_TAG" }
        return $env:NEXUS_RELEASE_TAG
    }
    # The latest release's tag from the redirect of github.com/<repo>/releases/latest (no API quota); API as fallback.
    try {
        $request = [Net.HttpWebRequest]::Create("https://github.com/$Repository/releases/latest")
        $request.AllowAutoRedirect = $false
        $request.UserAgent = 'NexusFree-get'
        $response = $request.GetResponse()
        try { $location = $response.Headers['Location'] } finally { $response.Close() }
        if ($location -match '/releases/tag/(v[0-9][^/?#]*)$') { return $Matches[1] }
    } catch { Write-Verbose "Redirect lookup failed: $($_.Exception.Message)" }
    return (Invoke-RestMethod -UseBasicParsing "https://api.github.com/repos/$Repository/releases/latest").tag_name
}

function Get-ExpectedHash([byte[]]$Manifest, [string]$Name) {
    $text = [Text.UTF8Encoding]::new($false, $true).GetString($Manifest).TrimStart([char]0xfeff)
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    $expected = $null
    foreach ($line in ($text -split '\r?\n' | Where-Object { $_.Trim() })) {
        if ($line -cnotmatch '^([0-9a-f]{64})\s+\*?(\S+)\s*$') { throw 'SHA256SUMS.txt has an invalid entry: installation refused.' }
        if (-not $seen.Add($Matches[2])) { throw 'SHA256SUMS.txt lists a file more than once: installation refused.' }
        if ($Matches[2] -ceq $Name) { $expected = $Matches[1] }
    }
    if (-not $expected) { throw "$Name is absent from SHA256SUMS.txt: installation refused." }
    return $expected
}

$rid = Get-Architecture
$name = "NexusFree-$rid.zip"
$work = Join-Path ([IO.Path]::GetTempPath()) "nexusfree-get-$PID"
New-Item -ItemType Directory -Force -Path $work | Out-Null
try {
    $tag = Get-LatestTag
    $base = "https://github.com/$Repository/releases/download/$tag"
    Write-Host "Downloading NexusFree $tag ($rid)..."
    $package = Join-Path $work $name
    Invoke-WebRequest -UseBasicParsing "$base/$name" -OutFile $package
    $manifest = (Invoke-WebRequest -UseBasicParsing "$base/SHA256SUMS.txt").RawContentStream.ToArray()
    $expected = Get-ExpectedHash $manifest $name
    $actual = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($expected -ne $actual) { throw "SHA-256 mismatch for $($name): the download is not the published package." }

    $unpacked = Join-Path $work 'package'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($package, $unpacked)
    $exe = Join-Path $unpacked 'NexusFree.exe'
    if (-not (Test-Path -LiteralPath $exe) -or -not (Test-Path -LiteralPath (Join-Path $unpacked 'install.ps1'))) { throw 'Not a NexusFree package.' }
    $version = (& $exe --version | Select-Object -First 1) -replace '^NexusFree v(\S+).*$', '$1'
    if ($version -notmatch '^\d+\.\d+\.\d+') { throw 'The package does not start on this PC.' }

    $releases = Join-Path $env:USERPROFILE '.nexus\nexus-free\releases'
    $target = Join-Path $releases $version
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $releases | Out-Null
    Move-Item -LiteralPath $unpacked -Destination $target
    Get-ChildItem -LiteralPath $target -Recurse -File | Unblock-File
    Get-ChildItem -LiteralPath $releases -Directory | Where-Object { $_.Name -ne $version } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $target 'install.ps1') -Apply
    if ($LASTEXITCODE) { throw "install.ps1 failed ($LASTEXITCODE)." }
    Write-Host "NexusFree $version installed." -ForegroundColor Green
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
