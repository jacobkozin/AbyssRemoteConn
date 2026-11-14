@echo off
setlocal
cd /d "%~dp0"
title Abyss Remote - Private LAN
rem Opens the Abyss Remote window in home-network mode: devices on your private network can connect.
rem The window's "Phone setup" list shows whether the firewall rule (ENABLE-LAN-FIREWALL.bat) is in place.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Launch.ps1" -LAN %*
if errorlevel 1 pause
