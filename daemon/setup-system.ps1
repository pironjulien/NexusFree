# setup-system.ps1
# Privileged setup of NexusFree (run elevated, by install.ps1): installs the native SYSTEM dispatcher
# (NexusFree.exe --dispatch) in %ProgramFiles%\NexusFree, creates the spool in %ProgramData%\NexusFree with explicit
# ACLs, and registers the SYSTEM scheduled task "NexusFree".
#
# One Windows account is authorized: the one that ran install.ps1 (-UserSid). Only that account, SYSTEM and
# Administrators can drop a request in the inbox, read the answers or start the task. Every other account on the PC
# gets nothing. SIDs are used throughout, so it works whatever the Windows display language.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$UserSid,
    # The engine to install: the one next to install.ps1 in a release package by default.
    [string]$EnginePath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'NexusFree.exe')
)
$ErrorActionPreference = 'Stop'

$user = [Security.Principal.SecurityIdentifier]::new($UserSid)  # throws on a malformed SID
$system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$admins = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')

if (-not (Test-Path -LiteralPath $EnginePath -PathType Leaf)) { throw "Engine not found: $EnginePath" }
$product = (Get-Item -LiteralPath $EnginePath).VersionInfo.ProductName
if ($product -ne 'NexusFree') { throw "Not the NexusFree engine: $EnginePath ($product)" }

$installFilesDir = Join-Path $env:ProgramFiles 'NexusFree'
$installedEngine = Join-Path $installFilesDir 'NexusFree.exe'
$configFile = Join-Path $installFilesDir 'config.json'
$spoolDir = Join-Path $env:ProgramData 'NexusFree'
$taskName = 'NexusFree'

# %ProgramFiles%\NexusFree: the dispatcher and the authorized account, writable by SYSTEM and Administrators only.
New-Item -ItemType Directory -Force -Path $installFilesDir | Out-Null
# A dispatcher still running (this very setup sent through NexusFree runs inside it) keeps its file: Windows lets a
# running program be renamed, not replaced. Earlier renamed copies go once nothing runs them any more.
Get-ChildItem -LiteralPath $installFilesDir -Filter 'NexusFree.exe.old-*' -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $installedEngine) {
    try { Remove-Item -LiteralPath $installedEngine -Force }
    catch { Rename-Item -LiteralPath $installedEngine -NewName "NexusFree.exe.old-$([guid]::NewGuid().ToString('N').Substring(0, 8))" }
}
Copy-Item -LiteralPath $EnginePath -Destination $installedEngine -Force
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

# The task runs the engine itself: no PowerShell host between Task Scheduler and the dispatcher.
$a = New-ScheduledTaskAction -Execute $installedEngine -Argument '--dispatch'
$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::FromMinutes(30)) -MultipleInstances Parallel
$p = New-ScheduledTaskPrincipal -UserId 'S-1-5-18' -LogonType ServiceAccount -RunLevel Highest

# Never stopped first: an update sent through NexusFree itself runs inside this very task.
Register-ScheduledTask -TaskName $taskName -Action $a -Settings $s -Principal $p -Description 'NexusFree: runs the requests of the authorized account under SYSTEM' -Force | Out-Null

# Who may start the task: SYSTEM, Administrators and the authorized account (read and run), nobody else.
$svc = New-Object -ComObject Schedule.Service
$svc.Connect()
$svc.GetFolder('\').GetTask($taskName).SetSecurityDescriptor("D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FRFX;;;$($user.Value))", 0)

# The PowerShell dispatcher of 1.2.5 and earlier, replaced by the engine.
Remove-Item -LiteralPath (Join-Path $installFilesDir 'Dispatch-NexusFree.ps1') -Force -ErrorAction SilentlyContinue

Write-Host "NexusFree SYSTEM task configured for $($user.Value)."
