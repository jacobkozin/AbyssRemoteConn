@echo off
setlocal
cd /d "%~dp0"
title Abyss Remote R3.3 - Build Windows
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build-Windows.ps1"
if errorlevel 1 (echo. & echo BUILD FAILED. Check the error above.) else (echo. & echo Build complete.)
pause
