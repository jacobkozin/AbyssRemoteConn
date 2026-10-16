@echo off
setlocal
cd /d "%~dp0"
title Abyss Remote R3.3 - HTTPS Startup Check
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Smoke-Test.ps1"
if errorlevel 1 (echo. & echo TEST FAILED. Copy the log from this window.)
pause
