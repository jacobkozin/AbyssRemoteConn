@echo off
setlocal
cd /d "%~dp0"
title Abyss Remote - Desktop shortcut
rem Puts an "Abyss Remote" shortcut to dist\win-x64\AbyssRemote.exe on your Desktop (building the program first if needed).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Create-Shortcut.ps1"
if errorlevel 1 (echo. & echo The shortcut was not created. The cause is printed above.)
pause
