@echo off
setlocal
cd /d "%~dp0"
title Abyss Remote - Windows LAN Host
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Launch.ps1" -LAN
if errorlevel 1 pause
