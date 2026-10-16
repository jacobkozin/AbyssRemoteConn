@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -Command "Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList '-NoProfile -ExecutionPolicy Bypass -NoExit -File ""%~dp0scripts\Firewall-Enable-LAN.ps1""'"
if errorlevel 1 (
 echo Could not enable LAN firewall access. See README.md.
 pause
)
