@echo off
setlocal
cd /d "%~dp0"
title Abyss Remote - Refresh LAN Certificate
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Launch.ps1" -LAN -RenewCert
if errorlevel 1 pause
