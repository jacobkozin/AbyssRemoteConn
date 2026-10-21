@echo off
setlocal
cd /d "%~dp0"
title Abyss Remote - HTTPS self-test
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Smoke-Test.ps1"
if errorlevel 1 (echo. & echo SELF-TEST FAILED. The cause is printed above.)
pause
