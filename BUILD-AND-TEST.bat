@echo off
setlocal
cd /d "%~dp0"
title Abyss Remote - Build and Test
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build-And-Test.ps1"
if errorlevel 1 goto failed
echo.
echo SUCCESS. Start dist\win-x64\AbyssRemote.exe, or run CREATE-DESKTOP-SHORTCUT.bat for a shortcut.
pause
exit /b 0
:failed
echo.
echo FAILED. The cause is printed above.
pause
exit /b 1
