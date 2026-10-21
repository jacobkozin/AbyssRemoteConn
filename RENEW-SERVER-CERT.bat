@echo off
setlocal
cd /d "%~dp0"
title Abyss Remote - New server certificate
rem Issues a fresh server certificate from the same root (phones keep trusting it), then starts in LAN mode.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Launch.ps1" -LAN -RenewCert %*
if errorlevel 1 pause
