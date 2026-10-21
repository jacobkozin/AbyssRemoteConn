@echo off
setlocal
cd /d "%~dp0"
title Abyss Remote - This PC only
rem Extra options are passed to the host, for example: START-LOCAL-ONLY.bat --port 8555
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Launch.ps1" %*
if errorlevel 1 pause
