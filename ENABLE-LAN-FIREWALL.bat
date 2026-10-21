@echo off
setlocal
cd /d "%~dp0"
rem Adds ONE Windows Firewall rule: TCP 8444 (or the port you pass, e.g. ENABLE-LAN-FIREWALL.bat 8555),
rem Private networks only, local subnet only. Asks for administrator rights.
set "PORTARG="
if not "%~1"=="" set "PORTARG=-Port %~1"
powershell.exe -NoProfile -Command "Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList '-NoProfile -ExecutionPolicy Bypass -NoExit -File ""%~dp0scripts\Firewall-Enable-LAN.ps1"" %PORTARG%'"
if errorlevel 1 (
  echo Could not update the firewall. See README.md.
  pause
)
