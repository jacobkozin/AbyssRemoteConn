@echo off
setlocal
cd /d "%~dp0"
title Abyss Remote - Publish
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build-Windows.ps1"
if errorlevel 1 (echo. & echo BUILD FAILED. The cause is printed above.) else (echo. & echo Build complete: dist\win-x64\AbyssRemote.Host.exe)
pause
