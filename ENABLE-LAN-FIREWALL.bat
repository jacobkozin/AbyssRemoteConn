@echo off
setlocal
cd /d "%~dp0"
rem Adds ONE Windows Firewall rule: TCP 8444 (or the port you pass, e.g. ENABLE-LAN-FIREWALL.bat 8555),
rem Private networks only, local subnet only. Rules for other ports are left alone.
rem Changing the firewall needs administrator rights. If this window does not have them yet, it
rem asks Windows for them (the UAC prompt) and runs itself again with the same port.

rem Windows tools are started by their full path: this file may run with administrator
rem rights, and a program with the same name in this folder must never be picked up instead.
set "ABYSS_FW_POWERSHELL=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"

set "ABYSS_FW_PORT=%~1"
if "%ABYSS_FW_PORT%"=="" set "ABYSS_FW_PORT=8444"
rem Anything left after taking the digits away means it is not a plain number.
set "ABYSS_FW_BAD="
for /f "delims=0123456789" %%A in ("%ABYSS_FW_PORT%") do set "ABYSS_FW_BAD=1"
if defined ABYSS_FW_BAD (
  echo The port must be a number, for example: ENABLE-LAN-FIREWALL.bat 8444
  pause
  exit /b 1
)

rem A window with administrator rights carries the "High" (or "System") integrity level.
"%SystemRoot%\System32\whoami.exe" /groups | "%SystemRoot%\System32\findstr.exe" /c:"S-1-16-12288" /c:"S-1-16-16384" >nul 2>&1
if not errorlevel 1 goto elevated

if "%~2"=="elevated" (
  echo This window still has no administrator rights, so the firewall was not changed.
  pause
  exit /b 1
)
rem This file's path travels in an environment variable and the port is only digits, so
rem spaces or quotes in the folder name cannot break the command.
set "ABYSS_FW_SELF=%~f0"
"%ABYSS_FW_POWERSHELL%" -NoProfile -Command "Start-Process -FilePath $env:ABYSS_FW_SELF -ArgumentList $env:ABYSS_FW_PORT, 'elevated' -Verb RunAs"
if errorlevel 1 (
  echo The administrator prompt was closed, so the firewall was not changed.
  pause
  exit /b 1
)
exit /b 0

:elevated
"%ABYSS_FW_POWERSHELL%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Firewall-Enable-LAN.ps1" -Port %ABYSS_FW_PORT%
if errorlevel 1 (
  echo.
  echo Could not update the firewall. The cause is printed above.
)
echo.
pause
