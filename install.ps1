# install.ps1
# Installs NexusFree for the current Windows account. The engine does the work (NexusFree.exe --install, see
# src\NexusFree\Installer.cs and SystemSetup.cs): itself in %USERPROFILE%\.nexus\nexus-free, then, with -Apply, the
# SYSTEM side (the engine as dispatcher in %ProgramFiles%\NexusFree, spool ACLs, scheduled task) and Antigravity's
# plugin. Claude Code and Codex get the plugin from the NexusSystem catalog.
#
# Run it as the account that will use NexusFree, not from an elevated prompt of another account: that account is the
# only one authorized. The SYSTEM side needs one elevation: through NexusFree itself when it is already installed for
# this account (update), otherwise one UAC prompt.
#
# From a release package, NexusFree.exe sits next to this script; from the source repository, it is compiled here
# (Native AOT, Visual Studio's C++ build tools needed).
#
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 [-Apply]
[CmdletBinding()]
param(
    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$engine = Join-Path $PSScriptRoot 'NexusFree.exe'
$built = $null
if (-not (Test-Path -LiteralPath $engine)) {
    $built = Join-Path ([IO.Path]::GetTempPath()) "nexusfree-publish-$PID"
    # Native AOT finds the C++ linker through vswhere, which is not on PATH outside a developer prompt.
    $env:PATH = "$env:PATH;$(Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer')"
    $rid = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }
    dotnet publish (Join-Path $PSScriptRoot 'src\NexusFree\NexusFree.csproj') -c Release -r $rid -p:DebugType=none -o $built --nologo | Out-Null
    if ($LASTEXITCODE) { throw 'dotnet publish failed.' }
    $engine = Join-Path $built 'NexusFree.exe'
}
try {
    if ($Apply) { & $engine --install --apply } else { & $engine --install }
    if ($LASTEXITCODE) { throw "NexusFree installation failed ($LASTEXITCODE)." }
} finally {
    if ($built) { Remove-Item -LiteralPath $built -Recurse -Force -ErrorAction SilentlyContinue }
}
