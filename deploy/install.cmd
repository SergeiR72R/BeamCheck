@echo off
rem Double-click to install BeamCheck (the window stays open to show the result).
rem Works from a network folder too (\\server\share\BeamCheck\install.cmd): the update check is set up automatically then.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -SharePath "%~dp0."
echo.
pause
