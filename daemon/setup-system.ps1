# setup-system.ps1
# Script d'enregistrement privilégié pour la tâche planifiée SYSTEM NexusFree
$ErrorActionPreference = 'Stop'

$installFilesDir = Join-Path $env:ProgramFiles 'NexusFree'
$installedDispatcher = Join-Path $installFilesDir 'Dispatch-NexusFree.ps1'
$sourceDispatcher = "C:\Users\julie\OneDrive\Documents\NexusHarness\NexusFree\daemon\Dispatch-NexusFree.ps1"

$spoolDir = Join-Path $env:ProgramData 'NexusFree'
$inboxDir = Join-Path $spoolDir 'inbox'
$stagingDir = Join-Path $spoolDir 'staging'
$outboxDir = Join-Path $spoolDir 'outbox'
$logDir = Join-Path $spoolDir 'logs'
$taskName = 'NexusFree'

New-Item -ItemType Directory -Force -Path $installFilesDir | Out-Null
Copy-Item -LiteralPath $sourceDispatcher -Destination $installedDispatcher -Force

foreach ($d in @($inboxDir, $stagingDir, $outboxDir, $logDir)) {
    if (-not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }
}

$aclStaging = New-Object System.Security.AccessControl.DirectorySecurity
$aclStaging.SetAccessRuleProtection($true, $false)
$ruleSys = New-Object System.Security.AccessControl.FileSystemAccessRule('AUTORITE NT\Système', 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
$ruleAdm = New-Object System.Security.AccessControl.FileSystemAccessRule('BUILTIN\Administrateurs', 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
$aclStaging.AddAccessRule($ruleSys)
$aclStaging.AddAccessRule($ruleAdm)
Set-Acl -LiteralPath $stagingDir -AclObject $aclStaging

$pwshExe = (Get-Command pwsh.exe -ErrorAction SilentlyContinue)?.Source
if (-not $pwshExe) { $pwshExe = 'pwsh.exe' }

$a = New-ScheduledTaskAction -Execute $pwshExe -Argument "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$installedDispatcher`""
$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::FromMinutes(30)) -MultipleInstances Parallel
$p = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest

Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
Register-ScheduledTask -TaskName $taskName -Action $a -Settings $s -Principal $p -Description 'Moteur d elevation souverain NexusFree pour la Trinite (NexusHarness)' -Force | Out-Null

$svc = New-Object -ComObject Schedule.Service
$svc.Connect()
$svc.GetFolder('\').GetTask($taskName).SetSecurityDescriptor('D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;FRFX;;;AU)', 0)

Write-Host "NexusFree SYSTEM task configured successfully."
