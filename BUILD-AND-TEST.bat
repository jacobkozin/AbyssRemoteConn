@echo off
setlocal
cd /d "%~dp0"
title Abyss Remote R3.3 - Build and Verify
echo.
echo === Abyss Remote R3.3 - Clean Build and HTTPS Self-Test ===
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build-Windows.ps1"
if errorlevel 1 goto failed
echo.
echo === Checking the real HTTPS host ===
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Smoke-Test.ps1"
if errorlevel 1 goto failed
echo.
echo SUCCESS. Now run START-LOCAL-ONLY.bat to use Abyss Remote.
pause
exit /b 0
:failed
echo.
echo FAILED. The cause is printed above. Copy the full window output.
pause
exit /b 1
