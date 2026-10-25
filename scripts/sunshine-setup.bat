@echo off
rem Sunshine setup GUI launcher (double-click me, no visible console)
rem Elevates via UAC automatically inside the PowerShell script.
start "" /min powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "%~dp0sunshine-setup.ps1"
exit
