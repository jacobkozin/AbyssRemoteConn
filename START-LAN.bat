@echo off
setlocal
cd /d "%~dp0"
title Abyss Remote - Private LAN
rem Devices on your private network can connect. Run ENABLE-LAN-FIREWALL.bat once first.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Launch.ps1" -LAN %*
if errorlevel 1 pause
