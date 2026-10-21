@echo off
setlocal
cd /d "%~dp0"
title Abyss Remote - Build and Test
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build-And-Test.ps1"
if errorlevel 1 goto failed
echo.
echo SUCCESS. Run START-LOCAL-ONLY.bat to use Abyss Remote on this PC.
pause
exit /b 0
:failed
echo.
echo FAILED. The cause is printed above.
pause
exit /b 1
