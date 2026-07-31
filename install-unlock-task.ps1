[CmdletBinding()]
param(
    [switch]$Remove
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$taskName = 'Windows Unlock Animation'
$launcher = Join-Path $PSScriptRoot 'UnlockAnimationLauncher.exe'
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$accountName = $identity.Name

$scheduler = New-Object -ComObject 'Schedule.Service'
$scheduler.Connect()
$rootFolder = $scheduler.GetFolder('\')

if ($Remove) {
    try {
        $rootFolder.DeleteTask($taskName, 0)
        Write-Host "Removed scheduled task: $taskName"
    }
    catch {
        if ($_.Exception.HResult -eq -2147024894) {
            Write-Host "Scheduled task is already absent: $taskName"
        }
        else {
            throw
        }
    }
    return
}

if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
    throw "Missing compiled launcher: $launcher. Run build-launcher.ps1 first."
}

$definition = $scheduler.NewTask(0)

$definition.RegistrationInfo.Author = $accountName
$definition.RegistrationInfo.Description =
    'Covers all displays and plays the animation after workstation unlock.'

$definition.Principal.UserId = $accountName
$definition.Principal.LogonType = 3       # TASK_LOGON_INTERACTIVE_TOKEN
$definition.Principal.RunLevel = 0        # TASK_RUNLEVEL_LUA

$definition.Settings.Enabled = $true
$definition.Settings.AllowDemandStart = $true
$definition.Settings.AllowHardTerminate = $true
$definition.Settings.DisallowStartIfOnBatteries = $false
$definition.Settings.StopIfGoingOnBatteries = $false
$definition.Settings.ExecutionTimeLimit = 'PT1M'
$definition.Settings.MultipleInstances = 2 # TASK_INSTANCES_IGNORE_NEW
$definition.Settings.StartWhenAvailable = $false

$unlockTrigger = $definition.Triggers.Create(11) # TASK_TRIGGER_SESSION_STATE_CHANGE
$unlockTrigger.Id = 'OnWorkstationUnlock'
$unlockTrigger.Enabled = $true
$unlockTrigger.StateChange = 8                    # TASK_SESSION_UNLOCK
$unlockTrigger.UserId = $accountName

$action = $definition.Actions.Create(0)           # TASK_ACTION_EXEC
$action.Id = 'PlayUnlockAnimation'
$action.Path = $launcher
$action.Arguments = ''
$action.WorkingDirectory = $PSScriptRoot

# TASK_CREATE_OR_UPDATE (6), registered for the current interactive user (3).
$registeredTask = $rootFolder.RegisterTaskDefinition(
    $taskName,
    $definition,
    6,
    $accountName,
    $null,
    3,
    $null)

Write-Host "Installed and enabled scheduled task: $($registeredTask.Name)"
Write-Host "Trigger: workstation unlock for $accountName"
Write-Host "Action: $launcher"
