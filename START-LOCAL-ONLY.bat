@echo off
setlocal
cd /d "%~dp0"
title Abyss Remote - Local Only
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Launch.ps1"
if errorlevel 1 pause
