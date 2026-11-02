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
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
}

function Write-BridgeLog([string]$Message, [string]$Level = 'INFO') {
    $timestamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    $line = "[$timestamp] [$Level] $Message"
    Add-Content -LiteralPath $logFile -Value $line -Encoding utf8 -ErrorAction SilentlyContinue
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
        $raw = Get-Content -LiteralPath $targetStaging -Raw -Encoding utf8
        $req = $raw | ConvertFrom-Json

        $cmd = [string]$req.command
        if ([string]::IsNullOrWhiteSpace($cmd)) {
            throw "Champ 'command' manquant ou vide."
        }

        Write-BridgeLog "Exécution commande requête $id : $cmd" 'INFO'

        $pwshExe = (Get-Command pwsh.exe -ErrorAction SilentlyContinue)?.Source
        if (-not $pwshExe) { $pwshExe = "$env:windir\System32\WindowsPowerShell\v1.0\powershell.exe" }

        $stdoutFile = Join-Path $stagingDir "$id.stdout"
        $stderrFile = Join-Path $stagingDir "$id.stderr"

        $proc = Start-Process -FilePath $pwshExe -ArgumentList @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', $cmd) -Wait -PassThru -NoNewWindow -RedirectStandardOutput $stdoutFile -RedirectStandardError $stderrFile

        $exitCode = $proc.ExitCode
        $stdoutText = if (Test-Path -LiteralPath $stdoutFile) { Get-Content -LiteralPath $stdoutFile -Raw -Encoding utf8 } else { '' }
        $stderrText = if (Test-Path -LiteralPath $stderrFile) { Get-Content -LiteralPath $stderrFile -Raw -Encoding utf8 } else { '' }

        Remove-Item -LiteralPath $stdoutFile -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $stderrFile -Force -ErrorAction SilentlyContinue

        $output = if ($stdoutText -and $stderrText) { "$stdoutText`n$stderrText" } elseif ($stdoutText) { $stdoutText } else { $stderrText }
        $success = ($exitCode -eq 0)

        Write-BridgeLog "Fin exécution requête $id (ExitCode: $exitCode)" 'INFO'
    } catch {
        $success = $false
        $errorMsg = $_.Exception.Message
        $exitCode = 1
        Write-BridgeLog "Erreur exécution requête $id : $errorMsg" 'ERROR'
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
