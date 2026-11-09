@echo off
rem Updates this folder from GitHub (the main branch). Double-click to run.
cd /d "%~dp0.."
echo Updating Lightlag code from GitHub...
git checkout main
git pull --ff-only
if errorlevel 1 (
  echo.
  echo Update failed. If you changed files here yourself, that can block it.
) else (
  echo.
  echo Up to date.
)
pause
