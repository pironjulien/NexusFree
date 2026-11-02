# setup-system.ps1
# Privileged setup of NexusFree (run elevated, by install.ps1): installs the SYSTEM dispatcher, creates the spool in
# %ProgramData%\NexusFree with explicit ACLs, and registers the SYSTEM scheduled task "NexusFree".
#
# One Windows account is authorized: the one that ran install.ps1 (-UserSid). Only that account, SYSTEM and
# Administrators can drop a request in the inbox, read the answers or start the task. Every other account on the PC
# gets nothing. SIDs are used throughout, so it works whatever the Windows display language.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$UserSid,
    [string]$SourceDir = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'

$user = [Security.Principal.SecurityIdentifier]::new($UserSid)  # throws on a malformed SID
$system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$admins = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')

$installFilesDir = Join-Path $env:ProgramFiles 'NexusFree'
$installedDispatcher = Join-Path $installFilesDir 'Dispatch-NexusFree.ps1'
$configFile = Join-Path $installFilesDir 'config.json'
$sourceDispatcher = Join-Path $SourceDir 'Dispatch-NexusFree.ps1'
if (-not (Test-Path -LiteralPath $sourceDispatcher)) { throw "Dispatcher not found: $sourceDispatcher" }

$spoolDir = Join-Path $env:ProgramData 'NexusFree'
$taskName = 'NexusFree'

# %ProgramFiles%\NexusFree: dispatcher and the authorized account, writable by SYSTEM and Administrators only.
New-Item -ItemType Directory -Force -Path $installFilesDir | Out-Null
Copy-Item -LiteralPath $sourceDispatcher -Destination $installedDispatcher -Force
[IO.File]::WriteAllText($configFile, (@{ userSid = $user.Value } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))

function Rule([Security.Principal.SecurityIdentifier]$Sid, [string]$Rights) {
    [Security.AccessControl.FileSystemAccessRule]::new($Sid, $Rights, 'ContainerInherit,ObjectInherit', 'None', 'Allow')
}

# Protected ACL (nothing inherited from %ProgramData%, which lets every user create files).
function Set-SpoolAcl([string]$Path, [string]$UserRights) {
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetOwner($admins)
    $acl.SetAccessRuleProtection($true, $false)
    $acl.AddAccessRule((Rule $system 'FullControl'))
    $acl.AddAccessRule((Rule $admins 'FullControl'))
    if ($UserRights) { $acl.AddAccessRule((Rule $user $UserRights)) }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

# The ACL of the root first, then each folder's own: a request carries its rights only while it is in the inbox.
Set-SpoolAcl $spoolDir 'ReadAndExecute'
Set-SpoolAcl (Join-Path $spoolDir 'inbox') 'Modify'
Set-SpoolAcl (Join-Path $spoolDir 'outbox') 'Modify'
Set-SpoolAcl (Join-Path $spoolDir 'logs') 'ReadAndExecute'
Set-SpoolAcl (Join-Path $spoolDir 'staging') $null

$pwshExe = Get-Command pwsh.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source
if (-not $pwshExe) { $pwshExe = "$env:windir\System32\WindowsPowerShell\v1.0\powershell.exe" }

$a = New-ScheduledTaskAction -Execute $pwshExe -Argument "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$installedDispatcher`""
$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::FromMinutes(30)) -MultipleInstances Parallel
$p = New-ScheduledTaskPrincipal -UserId 'S-1-5-18' -LogonType ServiceAccount -RunLevel Highest

# Never stopped first: an update sent through NexusFree itself runs inside this very task.
Register-ScheduledTask -TaskName $taskName -Action $a -Settings $s -Principal $p -Description 'NexusFree: runs the requests of the authorized account under SYSTEM' -Force | Out-Null

# Who may start the task: SYSTEM, Administrators and the authorized account (read and run), nobody else.
$svc = New-Object -ComObject Schedule.Service
$svc.Connect()
$svc.GetFolder('\').GetTask($taskName).SetSecurityDescriptor("D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FRFX;;;$($user.Value))", 0)

Write-Host "NexusFree SYSTEM task configured for $($user.Value)."
