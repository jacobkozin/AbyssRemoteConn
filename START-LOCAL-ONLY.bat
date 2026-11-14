@echo off
setlocal
cd /d "%~dp0"
title Abyss Remote - This PC only
rem Opens the Abyss Remote window in this-PC-only mode. Extra options are passed on, for example:
rem   START-LOCAL-ONLY.bat --port 8555      START-LOCAL-ONLY.bat --console
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Launch.ps1" %*
if errorlevel 1 pause
