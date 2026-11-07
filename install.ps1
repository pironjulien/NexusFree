# install.ps1
# Installs NexusFree for the current Windows account: the native engine in %USERPROFILE%\.nexus\nexus-free, then, with
# -Apply, the SYSTEM side (the engine as dispatcher, spool ACLs, scheduled task, see daemon\setup-system.ps1) and Antigravity's
# plugin. Claude Code and Codex get the plugin from the NexusSystem catalog.
#
# Run it as the account that will use NexusFree, not from an elevated prompt of another account: that account is the
# only one authorized. The SYSTEM side needs one elevation: through NexusFree itself when it is already installed for
# this account (update), otherwise one UAC prompt.
#
# From a release package, NexusFree.exe sits next to this script; from the source repository, it is built with dotnet.
#
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 [-Apply]
[CmdletBinding()]
param(
    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$taskName = 'NexusFree'
$binDir = Join-Path $env:USERPROFILE '.nexus\nexus-free'
$exePath = Join-Path $binDir 'NexusFree.exe'
$daemonDir = Join-Path $PSScriptRoot 'daemon'
$pluginDir = Join-Path $PSScriptRoot 'plugin'
$userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value

function Test-IsAdmin {
    $current = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    return $current.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# The SYSTEM side through the NexusFree already installed for this account: same protocol as NexusFree.exe (a request
# in the inbox, the task started, the answer read from the outbox).
function Invoke-ThroughNexusFree([string]$Command) {
    $spool = Join-Path $env:ProgramData 'NexusFree'
    $config = Join-Path $env:ProgramFiles 'NexusFree\config.json'
    if (-not (Test-Path -LiteralPath (Join-Path $spool 'inbox'))) { return $null }
    # 1.0 had no config.json and accepted every account: usable once, to install this version's restrictions.
    if (Test-Path -LiteralPath $config) {
        $authorized = try { [string](Get-Content -LiteralPath $config -Raw | ConvertFrom-Json).userSid } catch { '' }
        if ($authorized -ne $userSid) { return $null }
    }
    $id = 'req-' + [guid]::NewGuid().ToString('N')
    $request = @{ id = $id; command = $Command; created = (Get-Date).ToUniversalTime().ToString('o') } | ConvertTo-Json
    [IO.File]::WriteAllText((Join-Path $spool "inbox\$id.json"), $request, [Text.UTF8Encoding]::new($false))
    schtasks.exe /run /tn $taskName | Out-Null
    $answer = Join-Path $spool "outbox\$id.json"
    $deadline = (Get-Date).AddMinutes(5)
    while (-not (Test-Path -LiteralPath $answer)) {
        if ((Get-Date) -gt $deadline) { throw 'NexusFree did not answer within 5 minutes.' }
        Start-Sleep -Milliseconds 300
    }
    Start-Sleep -Milliseconds 200
    $result = Get-Content -LiteralPath $answer -Raw | ConvertFrom-Json
    Remove-Item -LiteralPath $answer -Force -ErrorAction SilentlyContinue
    return $result
}

function Invoke-Setup {
    $setup = Join-Path $daemonDir 'setup-system.ps1'
    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$setup`" -UserSid $userSid -EnginePath `"$exePath`""
    if (Test-IsAdmin) {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $setup -UserSid $userSid -EnginePath $exePath
        if ($LASTEXITCODE) { throw "setup-system.ps1 failed ($LASTEXITCODE)." }
        return
    }
    # Single quotes only: 1.0's dispatcher lost double quotes on the way to its PowerShell.
    function Quote([string]$Text) { "'" + $Text.Replace("'", "''") + "'" }
    $viaFree = Invoke-ThroughNexusFree "& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $(Quote $setup) -UserSid $userSid -EnginePath $(Quote $exePath)"
    if ($viaFree) {
        if (-not $viaFree.success) { throw "SYSTEM setup through NexusFree failed: $($viaFree.error) $($viaFree.output)" }
        return
    }
    $process = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList $arguments
    if ($process.ExitCode) { throw "SYSTEM setup failed or was declined ($($process.ExitCode))." }
}

Write-Host '=== NexusFree engine ==='
New-Item -ItemType Directory -Force -Path $binDir | Out-Null
$packaged = Join-Path $PSScriptRoot 'NexusFree.exe'
$built = $null
if (-not (Test-Path -LiteralPath $packaged)) {
    $built = Join-Path ([IO.Path]::GetTempPath()) "nexusfree-publish-$PID"
    dotnet publish (Join-Path $PSScriptRoot 'src\NexusFree\NexusFree.csproj') -c Release -r win-$($env:PROCESSOR_ARCHITECTURE.ToLower() -replace 'amd64', 'x64') --self-contained true -p:PublishSingleFile=true -o $built --nologo | Out-Null
    if ($LASTEXITCODE) { throw 'dotnet publish failed.' }
    $packaged = Join-Path $built 'NexusFree.exe'
}
# Engines of open AI sessions keep running the old file: renamed aside (Windows allows it), removed on a later run.
Get-ChildItem -LiteralPath $binDir -Filter 'NexusFree.exe.old-*' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $exePath) {
    try { Remove-Item -LiteralPath $exePath -Force } catch { Rename-Item -LiteralPath $exePath -NewName "NexusFree.exe.old-$([guid]::NewGuid().ToString('N').Substring(0, 8))" }
}
Copy-Item -LiteralPath $packaged -Destination $exePath -Force
if ($built) { Remove-Item -LiteralPath $built -Recurse -Force -ErrorAction SilentlyContinue }
if (-not (Test-Path -LiteralPath $exePath)) { throw "Engine missing: $exePath" }
Write-Host "Engine: $exePath"

if ($Apply) {
    Write-Host '=== SYSTEM side ==='
    Invoke-Setup
    Write-Host "SYSTEM task '$taskName' configured for this account ($userSid)."

    Write-Host '=== Antigravity plugin ==='
    $agyPluginDir = Join-Path $env:USERPROFILE '.gemini\config\plugins\nexus-free'
    New-Item -ItemType Directory -Force -Path (Join-Path $agyPluginDir 'skills\nexus-free') | Out-Null
    Copy-Item -LiteralPath (Join-Path $pluginDir 'plugin.json'), (Join-Path $pluginDir 'logo.png') -Destination $agyPluginDir -Force
    # Antigravity's guide in the Windows display language: French when Windows is in French, English otherwise.
    $skill = Join-Path $pluginDir 'skills\nexus-free\SKILL.md'
    $frenchSkill = Join-Path $PSScriptRoot 'fr\plugin\antigravity\skills\nexus-free\SKILL.md'
    if ((Get-UICulture).TwoLetterISOLanguageName -eq 'fr' -and (Test-Path -LiteralPath $frenchSkill)) { $skill = $frenchSkill }
    Copy-Item -LiteralPath $skill -Destination (Join-Path $agyPluginDir 'skills\nexus-free') -Force
    # Antigravity starts the command as written: the engine's absolute path for this account.
    $mcp = @{ mcpServers = @{ 'nexus-free' = @{ command = $exePath; args = @('--mcp') } } } | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText((Join-Path $agyPluginDir 'mcp_config.json'), $mcp, [Text.UTF8Encoding]::new($false))
    Write-Host "Antigravity plugin: $agyPluginDir"

    $status = & $exePath --status | ConvertFrom-Json
    if (-not $status.installed) { throw "NexusFree is not ready: $($status.taskStatus)" }
}

Write-Host 'NexusFree installed.'
