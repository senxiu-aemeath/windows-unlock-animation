[CmdletBinding()]
param(
    [switch]$Remove,
    [switch]$DoNotStart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$taskName = 'Windows Unlock Animation Resident'
$resident = Join-Path $PSScriptRoot 'UnlockAnimationResident.exe'
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$accountName = $identity.Name

$scheduler = New-Object -ComObject 'Schedule.Service'
$scheduler.Connect()
$rootFolder = $scheduler.GetFolder('\')

if ($Remove) {
    try {
        $existingTask = $rootFolder.GetTask($taskName)
        $existingTask.Stop(0)
    }
    catch {
    }

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

if (-not (Test-Path -LiteralPath $resident -PathType Leaf)) {
    throw "Missing resident executable: $resident. Run build-launcher.ps1 first."
}

$definition = $scheduler.NewTask(0)
$definition.RegistrationInfo.Author = $accountName
$definition.RegistrationInfo.Description =
    'Pre-stages black covers at session lock and plays the unlock animation when the user desktop becomes ready.'

$definition.Principal.UserId = $accountName
$definition.Principal.LogonType = 3        # TASK_LOGON_INTERACTIVE_TOKEN
$definition.Principal.RunLevel = 0         # TASK_RUNLEVEL_LUA

$definition.Settings.Enabled = $true
$definition.Settings.AllowDemandStart = $true
$definition.Settings.AllowHardTerminate = $true
$definition.Settings.DisallowStartIfOnBatteries = $false
$definition.Settings.StopIfGoingOnBatteries = $false
$definition.Settings.ExecutionTimeLimit = 'PT0S'
$definition.Settings.MultipleInstances = 2 # TASK_INSTANCES_IGNORE_NEW
$definition.Settings.RestartCount = 3
$definition.Settings.RestartInterval = 'PT1M'
$definition.Settings.StartWhenAvailable = $true

$logonTrigger = $definition.Triggers.Create(9) # TASK_TRIGGER_LOGON
$logonTrigger.Id = 'AtUserLogon'
$logonTrigger.Enabled = $true
$logonTrigger.UserId = $accountName

$action = $definition.Actions.Create(0)        # TASK_ACTION_EXEC
$action.Id = 'RunUnlockResident'
$action.Path = $resident
$action.Arguments = ''
$action.WorkingDirectory = $PSScriptRoot

$registeredTask = $rootFolder.RegisterTaskDefinition(
    $taskName,
    $definition,
    6,
    $accountName,
    $null,
    3,
    $null)

Write-Host "Installed and enabled scheduled task: $($registeredTask.Name)"
Write-Host "Trigger: logon for $accountName"
Write-Host "Action: $resident"

if (-not $DoNotStart) {
    $runningTask = $registeredTask.Run($null)
    Write-Host "Started resident instance: $($runningTask.InstanceGuid)"
}
