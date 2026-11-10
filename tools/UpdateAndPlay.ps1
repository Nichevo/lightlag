<#
.SYNOPSIS
  Lightlag "Update and Play": fetches the newest build if there is one, keeps the previous build for
  rollback, then starts the game.

.DESCRIPTION
  Install folder layout (default: the "play" folder next to this script, or this script's own folder
  once Install-Shortcuts.ps1 has copied it there):

    play\
      builds\current\     the build that runs
      builds\previous\    the build before it (use -Rollback to go back)
      data\               saves, logs, reports, crashes, nightly runs; never touched by updates
      UpdateAndPlay.ps1   this script (refreshed from each new build)

.PARAMETER Rollback      Swap back to the previous build, then play.
.PARAMETER NoUpdate      Play the current build without checking for a new one.
.PARAMETER NoLaunch      Update only (used by the nightly run).
.PARAMETER Force         Download the newest build even if it is already installed.
#>
[CmdletBinding()]
param(
    [switch]$Rollback,
    [switch]$NoUpdate,
    [switch]$NoLaunch,
    [switch]$Force,
    [string]$InstallDir,
    [string]$Repo = "Nichevo/lightlag"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"   # makes Invoke-WebRequest much faster

if (-not $InstallDir) {
    if (Test-Path (Join-Path $PSScriptRoot "builds")) { $InstallDir = $PSScriptRoot }
    else { $InstallDir = Join-Path $PSScriptRoot "..\play" }
}
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$InstallDir = (Resolve-Path $InstallDir).Path
$builds   = Join-Path $InstallDir "builds"
$current  = Join-Path $builds "current"
$previous = Join-Path $builds "previous"
$incoming = Join-Path $builds "incoming"
$data     = Join-Path $InstallDir "data"
New-Item -ItemType Directory -Force -Path $builds, $data | Out-Null

function Say($text, $color = "Gray") { Write-Host $text -ForegroundColor $color }

function Get-InstalledVersion($dir) {
    $file = Join-Path $dir "build_info.json"
    if (Test-Path $file) { return (Get-Content $file -Raw | ConvertFrom-Json).version }
    return $null
}

function Test-GameRunning {
    return [bool](Get-Process -Name "Lightlag" -ErrorAction SilentlyContinue)
}

function Start-Game {
    $exe = Join-Path $current "Lightlag.exe"
    if (-not (Test-Path $exe)) { throw "No build installed yet in $current. Run again when online." }
    Say "Starting Lightlag $(Get-InstalledVersion $current)..." Cyan
    Start-Process -FilePath $exe -WorkingDirectory $current -ArgumentList @("--", "`"--data-dir=$data`"")
}

try {
    if ($Rollback) {
        if (-not (Test-Path $previous)) { throw "There is no previous build to roll back to." }
        if (Test-GameRunning) { throw "Close Lightlag first." }
        $swap = Join-Path $builds "swap"
        Rename-Item $current $swap
        Rename-Item $previous $current
        Rename-Item $swap $previous
        Say "Rolled back to $(Get-InstalledVersion $current) (the newer build is kept as 'previous')." Yellow
        if (-not $NoLaunch) { Start-Game }
        return
    }

    $installed = Get-InstalledVersion $current
    if (-not $NoUpdate) {
        Say "Checking for a new build..."
        $headers = @{ "User-Agent" = "lightlag-updater"; "Accept" = "application/vnd.github+json" }
        $release = $null
        try {
            # A change merged a moment ago is still being built on GitHub (a few minutes): wait for it,
            # rather than starting the old build and only picking the new one up on the next run.
            $waitedSince = Get-Date
            try { while ($true) {
                $runs = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/actions/runs?branch=main&event=push&per_page=5" `
                    -Headers $headers -TimeoutSec 20
                $building = @($runs.workflow_runs | Where-Object { $_.status -ne "completed" -and "$($_.id)" -ne "$env:GITHUB_RUN_ID" })
                if ($building.Count -eq 0) { break }
                $minutes = [math]::Round(((Get-Date) - $waitedSince).TotalMinutes, 1)
                if ($minutes -ge 15) { Say "Still building after 15 minutes; playing what is installed." Yellow; break }
                if ($minutes -eq 0) { Say "A new build is being made on GitHub (usually about 4 minutes). Waiting for it... (Ctrl+C to stop)" Yellow }
                Start-Sleep -Seconds 20
            } } catch { Say "Could not check for a build in progress ($($_.Exception.Message))." Yellow }
            $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers $headers -TimeoutSec 20
        } catch {
            Say "Could not reach GitHub ($($_.Exception.Message)). Playing the installed build." Yellow
        }

        if ($release) {
            $latest = $release.tag_name.TrimStart("v")
            if ($latest -eq $installed -and -not $Force) {
                Say "Up to date: $installed" Green
            } elseif (Test-GameRunning) {
                Say "Build $latest is out, but Lightlag is running. Close it and run Update and Play again." Yellow
                if (-not $NoLaunch) { return }
            } else {
                $asset = $release.assets | Where-Object { $_.name -like "*-windows.zip" } | Select-Object -First 1
                if (-not $asset) { throw "Release $latest has no Windows zip." }
                Say "Downloading $($asset.name) ($([math]::Round($asset.size / 1MB)) MB)..."
                $zip = Join-Path $builds "download.zip"
                Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -UseBasicParsing
                if (Test-Path $incoming) { Remove-Item $incoming -Recurse -Force }
                Expand-Archive -Path $zip -DestinationPath $incoming -Force
                Remove-Item $zip -Force
                if (-not (Test-Path (Join-Path $incoming "Lightlag.exe"))) { throw "The download did not contain Lightlag.exe." }

                if (Test-Path $previous) { Remove-Item $previous -Recurse -Force }
                if (Test-Path $current) { Rename-Item $current $previous }
                Rename-Item $incoming $current
                Say "Installed $latest (was $(if ($installed) { $installed } else { 'nothing' }))." Green

                # Refresh the launcher scripts from the new build, so fixes to them arrive too.
                foreach ($f in Get-ChildItem $current -Filter "*.ps1") { Copy-Item $f.FullName $InstallDir -Force }
                foreach ($f in Get-ChildItem $current -Filter "*.cmd") { Copy-Item $f.FullName $InstallDir -Force }
            }
        }
    }

    if (-not $NoLaunch) { Start-Game }
}
catch {
    Say "Update and Play failed: $($_.Exception.Message)" Red
    $log = Join-Path $data "logs\updater.log"
    New-Item -ItemType Directory -Force -Path (Split-Path $log) | Out-Null
    Add-Content $log "$(Get-Date -Format o) $($_.Exception.Message)"
    if ($Host.Name -eq "ConsoleHost" -and -not $NoLaunch) { Read-Host "Press Enter to close" | Out-Null }
    exit 1
}
