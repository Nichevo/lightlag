<#
.SYNOPSIS
  One-time setup: creates the play folder, copies the launcher there, and adds desktop shortcuts
  "Lightlag" (update and play) and "Lightlag (previous build)".
  Run from the repo:  powershell -ExecutionPolicy Bypass -File tools\Install-Shortcuts.ps1
#>
param([string]$InstallDir = (Join-Path $PSScriptRoot "..\play"))

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path (Join-Path $InstallDir "builds"), (Join-Path $InstallDir "data") | Out-Null
$InstallDir = (Resolve-Path $InstallDir).Path
foreach ($f in "UpdateAndPlay.ps1", "UpdateAndPlay.cmd", "Run-Nightly.ps1", "Register-Nightly.ps1") {
    Copy-Item (Join-Path $PSScriptRoot $f) $InstallDir -Force
}

$shell = New-Object -ComObject WScript.Shell
$desktop = [Environment]::GetFolderPath("Desktop")
function New-Shortcut($name, $arguments) {
    $s = $shell.CreateShortcut((Join-Path $desktop "$name.lnk"))
    $s.TargetPath = "powershell.exe"
    $s.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$InstallDir\UpdateAndPlay.ps1`" $arguments"
    $s.WorkingDirectory = $InstallDir
    $exe = Join-Path $InstallDir "builds\current\Lightlag.exe"
    if (Test-Path $exe) { $s.IconLocation = $exe }
    $s.Save()
}
New-Shortcut "Lightlag" ""
New-Shortcut "Lightlag (previous build)" "-Rollback"

Write-Host "Done. Play folder: $InstallDir" -ForegroundColor Green
Write-Host "Desktop shortcuts: 'Lightlag' (update and play) and 'Lightlag (previous build)'."
Write-Host "Reports, crashes and logs go to $InstallDir\data"
