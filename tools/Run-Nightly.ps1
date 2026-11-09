<#
.SYNOPSIS
  Overnight headless run: updates to the newest build, then plays many AI games with strict rule
  checks and writes the morning summary to data\nightly\latest-summary.md, then runs a battle sweep
  (data\nightly\latest-battles.md).
#>
param(
    [int]$Games = 200,
    [int]$Days = 365,
    [int]$Minutes = 360,
    [string]$InstallDir = $PSScriptRoot
)

$ErrorActionPreference = "Continue"
$data = Join-Path $InstallDir "data"
New-Item -ItemType Directory -Force -Path (Join-Path $data "nightly") | Out-Null
$transcript = Join-Path $data "nightly\last-run-console.txt"
Start-Transcript -Path $transcript -Force | Out-Null
try {
    & (Join-Path $InstallDir "UpdateAndPlay.ps1") -NoLaunch -InstallDir $InstallDir
    $runner = Join-Path $InstallDir "builds\current\headless\Lightlag.Headless.exe"
    if (-not (Test-Path $runner)) { throw "Headless runner not found at $runner" }
    & $runner nightly --games $Games --days $Days --minutes $Minutes --data-dir $data
    Write-Host "Exit code: $LASTEXITCODE"
    # Battle sweep: every fleet mix, approach and missile brain, 10 times each (about a minute).
    & $runner battles --repeats 10 --data-dir $data
    Write-Host "Battle sweep exit code: $LASTEXITCODE"
}
catch {
    Write-Host "Nightly run failed: $($_.Exception.Message)"
    Set-Content (Join-Path $data "nightly\latest-summary.md") "# Lightlag overnight run FAILED TO START`n`n$($_.Exception.Message)`n`nSee last-run-console.txt."
}
finally {
    Stop-Transcript | Out-Null
}
