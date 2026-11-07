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
# For tests only: NEXUS_FREE_SOURCE (a local package instead of the latest release) with NEXUS_FREE_HOME (another folder
# instead of %USERPROFILE%\.nexus\nexus-free): the package is unpacked and checked there, install.ps1 is never run.
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

function Move-StagedFolder([string]$From, [string]$To) {
    # A folder cannot be renamed while any file inside it is open, whoever holds it. This installer never runs or keeps
    # open anything in the staged folder, but antivirus and indexing services open freshly written executables for a
    # moment to scan them, which made the rename fail with "being used by another process". Only that external,
    # transient hold (sharing, lock or access violation while the staged folder is still there) is waited out, at most
    # 10 s; any other error, or a longer hold, fails.
    $transient = 0x80070020, 0x80070021, 0x80070005  # ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION, ERROR_ACCESS_DENIED
    for ($attempt = 1; ; $attempt++) {
        try { [IO.Directory]::Move($From, $To); return }
        catch {
            $failure = $_.Exception
            while ($failure.InnerException -and $failure -is [Management.Automation.MethodInvocationException]) { $failure = $failure.InnerException }
            $held = ($failure -is [IO.IOException] -or $failure -is [UnauthorizedAccessException]) -and
                ($transient -contains $failure.HResult) -and (Test-Path -LiteralPath $From) -and -not (Test-Path -LiteralPath $To)
            if (-not $held -or $attempt -ge 40) { throw "Cannot move $From to $($To): $($failure.Message)" }
            Start-Sleep -Milliseconds 250
        }
    }
}

$rid = Get-Architecture
$name = "NexusFree-$rid.zip"
$local = [bool]$env:NEXUS_FREE_SOURCE
if ($local -and -not $env:NEXUS_FREE_HOME) { throw 'NEXUS_FREE_SOURCE is for tests only and needs NEXUS_FREE_HOME.' }
$home_ = if ($env:NEXUS_FREE_HOME) { $env:NEXUS_FREE_HOME } else { Join-Path $env:USERPROFILE '.nexus\nexus-free' }
$releases = Join-Path $home_ 'releases'
# Staged next to the releases folder (same volume: the final move is a rename), never inside it: every other folder
# in releases is removed as an older version.
New-Item -ItemType Directory -Force -Path $releases | Out-Null
$staged = Join-Path $home_ ('staging-' + [guid]::NewGuid().ToString('N'))
$work = Join-Path ([IO.Path]::GetTempPath()) "nexusfree-get-$PID"
New-Item -ItemType Directory -Force -Path $work | Out-Null
try {
    if ($local) {
        $package = (Resolve-Path -LiteralPath $env:NEXUS_FREE_SOURCE).Path
        Write-Host "Installing NexusFree from $package..."
    } else {
        $tag = Get-LatestTag
        $base = "https://github.com/$Repository/releases/download/$tag"
        Write-Host "Downloading NexusFree $tag ($rid)..."
        $package = Join-Path $work $name
        Invoke-WebRequest -UseBasicParsing "$base/$name" -OutFile $package
        $manifest = (Invoke-WebRequest -UseBasicParsing "$base/SHA256SUMS.txt").RawContentStream.ToArray()
        $expected = Get-ExpectedHash $manifest $name
        $actual = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($expected -ne $actual) { throw "SHA-256 mismatch for $($name): the download is not the published package." }
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($package, $staged)
    $exe = Join-Path $staged 'NexusFree.exe'
    if (-not (Test-Path -LiteralPath $exe) -or -not (Test-Path -LiteralPath (Join-Path $staged 'install.ps1'))) { throw 'Not a NexusFree package.' }
    Get-ChildItem -LiteralPath $staged -Recurse -File | Unblock-File
    # The version from the executable's version resource, without running it: a program just run from the staged
    # folder is what scanners open right after, when the folder has to be renamed.
    $version = "$((Get-Item -LiteralPath $exe).VersionInfo.ProductVersion)".Split('+')[0].Trim()
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Not a NexusFree package (no version).' }

    $target = Join-Path $releases $version
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    Move-StagedFolder $staged $target
    # Run once in place: the package must start on this PC's architecture and report the version it carries.
    # Its whole output first: Select-Object -First in the pipeline stops a native program early in Windows PowerShell,
    # whose exit code then reads -1.
    $output = @(& (Join-Path $target 'NexusFree.exe') --version)
    $answer = "$($output | Select-Object -First 1)" -replace '^NexusFree v(\S+).*$', '$1'
    if ($LASTEXITCODE -or $answer -ne $version) {
        Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue
        throw 'The package does not start on this PC.'
    }
    Get-ChildItem -LiteralPath $releases -Directory | Where-Object { $_.Name -ne $version } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }

    if ($local) { Write-Host "NexusFree $version is in $target (test: install.ps1 not run)." -ForegroundColor Green; return }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $target 'install.ps1') -Apply
    if ($LASTEXITCODE) { throw "install.ps1 failed ($LASTEXITCODE)." }
    Write-Host "NexusFree $version installed." -ForegroundColor Green
} finally {
    Remove-Item -LiteralPath $work, $staged -Recurse -Force -ErrorAction SilentlyContinue
}
