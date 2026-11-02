# install.ps1
# Installe et qualifie le produit NexusFree (binaire natif, sas SYSTEM, tâche planifiée et plugins IA).
# Usage : pwsh -NoProfile -File install.ps1 [-Apply]

[CmdletBinding()]
param(
    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$productName = 'NexusFree'
$taskName = 'NexusFree'
$binDir = Join-Path $env:USERPROFILE '.nexus\nexus-free'
$exePath = Join-Path $binDir 'NexusFree.exe'
$installFilesDir = Join-Path $env:ProgramFiles 'NexusFree'
$installedDispatcher = Join-Path $installFilesDir 'Dispatch-NexusFree.ps1'
$sourceDispatcher = Join-Path $PSScriptRoot 'daemon\Dispatch-NexusFree.ps1'

$spoolDir = Join-Path $env:ProgramData 'NexusFree'
$inboxDir = Join-Path $spoolDir 'inbox'
$stagingDir = Join-Path $spoolDir 'staging'
$outboxDir = Join-Path $spoolDir 'outbox'
$logDir = Join-Path $spoolDir 'logs'

function Test-IsAdmin {
    $current = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    return $current.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-PrivilegedScript([string]$ScriptBlockText) {
    if (Test-IsAdmin) {
        Invoke-Expression $ScriptBlockText
        return
    }

    # Tentative via le pont existant si actif
    $adminBridgeScript = "C:\Users\julie\OneDrive\Documents\NexusInfra\ops\workstation\Invoke-NexusAdminBridge.ps1"
    if (Test-Path -LiteralPath $adminBridgeScript) {
        $encoded = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($ScriptBlockText))
        $cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -EncodedCommand $encoded"
        $res = & $adminBridgeScript -Command $cmd
        if (-not $res.success) {
            throw "Échec installation privilégiée via pont : $($res.error)"
        }
        return
    }

    # Repli élévation UAC
    Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList @('-NoProfile', '-Command', $ScriptBlockText)
}

Write-Host "=== Publication de NexusFree (C# .NET) ==="
$srcProj = Join-Path $PSScriptRoot 'src\NexusFree\NexusFree.csproj'
New-Item -ItemType Directory -Force -Path $binDir | Out-Null

dotnet publish $srcProj -c Release -o $binDir --nologo | Out-Null
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Échec de publication : $exePath introuvable."
}
Write-Host "Binaire disponible : $exePath"

if ($Apply) {
    Write-Host "=== Configuration du moteur SYSTEM ==="
    $setupScript = Join-Path $PSScriptRoot 'daemon\setup-system.ps1'
    if (Test-IsAdmin) {
        & pwsh.exe -NoProfile -ExecutionPolicy Bypass -File $setupScript
    } else {
        $adminBridgeScript = "C:\Users\julie\OneDrive\Documents\NexusInfra\ops\workstation\Invoke-NexusAdminBridge.ps1"
        if (Test-Path -LiteralPath $adminBridgeScript) {
            $res = & $adminBridgeScript -Command "pwsh.exe -NoProfile -ExecutionPolicy Bypass -File `"$setupScript`""
            if (-not $res.success) {
                throw "Échec configuration SYSTEM : $($res.error)"
            }
        } else {
            Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $setupScript)
        }
    }
    Write-Host "Moteur SYSTEM '$taskName' configuré."

    Write-Host "=== Configuration du plugin Antigravity ==="
    $agyPluginDir = Join-Path $env:USERPROFILE '.gemini\config\plugins\nexus-free'
    $agySkillDir = Join-Path $agyPluginDir 'skills\nexus-free'
    New-Item -ItemType Directory -Force -Path $agySkillDir | Out-Null

    $pluginJson = [ordered]@{
        name = "nexus-free"
        displayName = "NexusFree"
        version = "1.0.0"
        description = "Moteur d'élévation souverain et exécution privilégiée SYSTEM sans UAC pour la Trinité (C# natif)."
        logo = "logo.png"
        author = [ordered]@{
            name = "Julien Piron"
            url = "https://julienpiron.fr"
        }
        keywords = @("elevation", "admin", "system", "uac", "mcp", "trinite")
    } | ConvertTo-Json -Depth 4
    Set-Content -LiteralPath (Join-Path $agyPluginDir 'plugin.json') -Value $pluginJson -Encoding utf8

    $mcpJson = [ordered]@{
        mcpServers = [ordered]@{
            "nexus-free" = [ordered]@{
                command = $exePath
                args = @("--mcp")
            }
        }
    } | ConvertTo-Json -Depth 4
    Set-Content -LiteralPath (Join-Path $agyPluginDir 'mcp_config.json') -Value $mcpJson -Encoding utf8

    $skillMd = @'
---
name: nexus-free
description: Exécuter toute commande ou script Windows nécessitant les privilèges Administrateur / SYSTEM sans invite UAC (sfc, dism, services, registre machine HKLM, drivers, installations).
---

# NexusFree (v1.0.0)

Serveur MCP d'élévation souveraine universelle partagé par la Trinité (Claude, Codex, Antigravity).

## Outils disponibles

### `free(command, timeout_seconds=600)`
Exécute n'importe quelle commande PowerShell ou binaire avec les droits `SYSTEM` sans aucune fenêtre UAC.
* `command` (requis) : chaîne de commande exacte à exécuter (ex. `sfc /scannow`, `Restart-Service wuauserv`, `Get-Process`).
* `timeout_seconds` (optionnel, défaut 600) : délai limite en secondes.
* Retour : code de sortie, `stdout`, `stderr`, durée d'exécution.

### `free_status()`
Vérifie la santé du pont SYSTEM, l'état de la tâche planifiée et du sas de communication.
'@
    Set-Content -LiteralPath (Join-Path $agySkillDir 'SKILL.md') -Value $skillMd -Encoding utf8
    Write-Host "Plugin Antigravity configuré : $agyPluginDir"

    Write-Host "=== Configuration du plugin Claude Code ==="
    $claudeConfig = Join-Path $env:APPDATA 'Claude\claude_desktop_config.json'
    if (Test-Path -LiteralPath $claudeConfig) {
        try {
            $cJson = Get-Content -LiteralPath $claudeConfig -Raw -Encoding utf8 | ConvertFrom-Json
            if (-not $cJson.mcpServers) { $cJson | Add-Member -MemberType NoteProperty -Name mcpServers -Value ([ordered]@{}) }
            $cJson.mcpServers | Add-Member -MemberType NoteProperty -Name "nexus-free" -Value ([ordered]@{
                command = $exePath
                args = @("--mcp")
            }) -Force
            $cJson | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $claudeConfig -Encoding utf8
            Write-Host "Plugin Claude configuré dans $claudeConfig"
        } catch {
            Write-Warning "Claude config mise à jour ignorée : $($_.Exception.Message)"
        }
    }

    # Recopie miroir dans le dossier plugin/ du dépôt pour distribution
    $repoPluginDir = Join-Path $PSScriptRoot 'plugin'
    New-Item -ItemType Directory -Force -Path (Join-Path $repoPluginDir 'skills\nexus-free') | Out-Null
    Copy-Item -LiteralPath (Join-Path $agyPluginDir 'plugin.json') -Destination (Join-Path $repoPluginDir 'plugin.json') -Force
    Copy-Item -LiteralPath (Join-Path $agyPluginDir 'mcp_config.json') -Destination (Join-Path $repoPluginDir 'mcp_config.json') -Force
    Copy-Item -LiteralPath (Join-Path $agySkillDir 'SKILL.md') -Destination (Join-Path $repoPluginDir 'skills\nexus-free\SKILL.md') -Force
}

Write-Host "NexusFree installation terminée avec succès."
