@echo off
rem Double-click to update Lightlag to the newest build and play. Add -Rollback to go back one build.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0UpdateAndPlay.ps1" %*
