<#
.SYNOPSIS
  Schedules Run-Nightly.ps1 every night (default 01:30) with Windows Task Scheduler.
  -Unregister removes it. Run it from the play folder (Install-Shortcuts.ps1 copies it there).
#>
param(
    [string]$At = "01:30",
    [int]$Games = 200,
    [int]$Minutes = 360,
    [switch]$Unregister
)

$ErrorActionPreference = "Stop"
$name = "Lightlag nightly run"
if ($Unregister) {
    Unregister-ScheduledTask -TaskName $name -Confirm:$false
    Write-Host "Removed '$name'."
    return
}
$script = Join-Path $PSScriptRoot "Run-Nightly.ps1"
$action = New-ScheduledTaskAction -Execute "powershell.exe" `
    -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`" -Games $Games -Minutes $Minutes" `
    -WorkingDirectory $PSScriptRoot
$trigger = New-ScheduledTaskTrigger -Daily -At $At
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -WakeToRun -ExecutionTimeLimit (New-TimeSpan -Minutes ($Minutes + 60))
Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null
Write-Host "Scheduled '$name' daily at $At. Results: $PSScriptRoot\data\nightly\latest-summary.md" -ForegroundColor Green
