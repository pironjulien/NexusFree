# Dispatch-NexusFree.ps1
# Exécuteur privilégié du pont d'élévation universel NexusFree.
# Tourne sous SYSTEM via la tâche planifiée « NexusFree ».
# Sas de travail : %ProgramData%\NexusFree

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$spoolDir = Join-Path $env:ProgramData 'NexusFree'
$inboxDir = Join-Path $spoolDir 'inbox'
$stagingDir = Join-Path $spoolDir 'staging'
$outboxDir = Join-Path $spoolDir 'outbox'
$logDir = Join-Path $spoolDir 'logs'
$logFile = Join-Path $logDir 'elevation.log'

foreach ($dir in @($inboxDir, $stagingDir, $outboxDir, $logDir)) {
    if (-not (Test-Path -LiteralPath $dir)) {
        exit 1  # jamais recréé ici : sans les ACL de setup-system.ps1, tout compte pourrait y écrire
    }
}

function Write-BridgeLog([string]$Message, [string]$Level = 'INFO') {
    $timestamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    $line = "[$timestamp] [$Level] $Message"
    Add-Content -LiteralPath $logFile -Value $line -Encoding utf8 -ErrorAction SilentlyContinue
}

# Compte autorisé par setup-system.ps1. Sans lui, rien ne s'exécute : l'ACL du sas seule ne suffit pas.
$configFile = Join-Path $PSScriptRoot 'config.json'
$authorized = try { [string](Get-Content -LiteralPath $configFile -Raw -Encoding utf8 | ConvertFrom-Json).userSid } catch { '' }
if (-not $authorized) {
    Write-BridgeLog "Aucun compte autorisé dans $configFile : relancer install.ps1 -Apply." 'ERROR'
    exit 1
}
# Une requête ne vaut que si son fichier appartient au compte autorisé (ou aux Administrateurs ou à SYSTEM : le
# propriétaire d'un fichier créé depuis une session élevée).
$owners = @($authorized, 'S-1-5-32-544', 'S-1-5-18')

# Sortie lue ligne à ligne en UTF-8 strict ; un programme natif qui ignore chcp 65001 sans console (dism, netsh...)
# écrit dans la page OEM du système : ses lignes y sont relues plutôt que rendues en caractères de remplacement.
function Read-CommandOutput([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return '' }
    $bytes = [IO.File]::ReadAllBytes($Path)
    $utf8 = [Text.UTF8Encoding]::new($false, $true)
    $oem = [Text.Encoding]::GetEncoding([Globalization.CultureInfo]::CurrentCulture.TextInfo.OEMCodePage)
    $text = [Text.StringBuilder]::new()
    $start = 0
    while ($start -lt $bytes.Length) {
        $end = [Array]::IndexOf($bytes, [byte]10, $start)
        if ($end -lt 0) { $end = $bytes.Length - 1 }
        $count = $end - $start + 1
        try { [void]$text.Append($utf8.GetString($bytes, $start, $count)) }
        catch [Text.DecoderFallbackException] { [void]$text.Append($oem.GetString($bytes, $start, $count)) }
        $start = $end + 1
    }
    return $text.ToString().TrimStart([char]0xFEFF)
}

$pending = @(Get-ChildItem -LiteralPath $inboxDir -Filter '*.json' -File -ErrorAction SilentlyContinue)
if ($pending.Count -eq 0) { exit 0 }

foreach ($item in $pending) {
    $id = $item.BaseName
    $targetStaging = Join-Path $stagingDir "$id.json"
    $targetOutbox = Join-Path $outboxDir "$id.json"

    try {
        Move-Item -LiteralPath $item.FullName -Destination $targetStaging -Force
    } catch {
        Write-BridgeLog "Échec déplacement vers staging pour $($item.Name): $($_.Exception.Message)" 'ERROR'
        continue
    }

    $success = $false
    $output = ''
    $exitCode = 0
    $errorMsg = ''
    $started = Get-Date

    try {
        $owner = (Get-Acl -LiteralPath $targetStaging).GetOwner([Security.Principal.SecurityIdentifier]).Value
        if ($owner -notin $owners) {
            throw "Requête refusée : son fichier appartient à $owner, pas au compte autorisé."
        }
        $raw = Get-Content -LiteralPath $targetStaging -Raw -Encoding utf8
        $req = $raw | ConvertFrom-Json

        $cmd = [string]$req.command
        if ([string]::IsNullOrWhiteSpace($cmd)) {
            throw "Champ 'command' manquant ou vide."
        }

        # Never persist command arguments: they can contain credentials or private administrative data.
        Write-BridgeLog "Exécution requête $id" 'INFO'

        $pwshExe = Get-Command pwsh.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source
        if (-not $pwshExe) { $pwshExe = "$env:windir\System32\WindowsPowerShell\v1.0\powershell.exe" }

        $stdoutFile = Join-Path $stagingDir "$id.stdout"
        $stderrFile = Join-Path $stagingDir "$id.stderr"

        # Commande encodée : passée telle quelle, guillemets compris (Start-Process recolle les arguments sans les citer).
        # Sortie en UTF-8, celle que lit le répartiteur, y compris pour les programmes natifs (whoami, sfc...).
        # Sans $ProgressPreference, la barre de progression des cmdlets arrive en CLIXML dans la sortie d'erreur.
        $prelude = '$ProgressPreference = ''SilentlyContinue''; $OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); chcp.com 65001 > $null; '
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($prelude + $cmd))
        $proc = Start-Process -FilePath $pwshExe -ArgumentList @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded) -Wait -PassThru -NoNewWindow -RedirectStandardOutput $stdoutFile -RedirectStandardError $stderrFile

        $exitCode = $proc.ExitCode
        $stdoutText = Read-CommandOutput $stdoutFile
        $stderrText = Read-CommandOutput $stderrFile

        Remove-Item -LiteralPath $stdoutFile -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $stderrFile -Force -ErrorAction SilentlyContinue

        $output = if ($stdoutText -and $stderrText) { "$stdoutText`n$stderrText" } elseif ($stdoutText) { $stdoutText } else { $stderrText }
        $success = ($exitCode -eq 0)

        Write-BridgeLog "Fin exécution requête $id (ExitCode: $exitCode)" 'INFO'
    } catch {
        $success = $false
        $errorMsg = $_.Exception.Message
        $exitCode = 1
        Write-BridgeLog "Erreur exécution requête $id ($($_.Exception.GetType().Name))" 'ERROR'
    } finally {
        $finished = Get-Date
        $response = [ordered]@{
            id        = $id
            success   = $success
            exitCode  = $exitCode
            output    = $output
            error     = $errorMsg
            started   = $started.ToString('o')
            finished  = $finished.ToString('o')
        }
        $response | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $targetOutbox -Encoding utf8
        Remove-Item -LiteralPath $targetStaging -Force -ErrorAction SilentlyContinue
    }
}
