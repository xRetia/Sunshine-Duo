@echo off
rem Sunshine setup GUI launcher (double-click me)
rem Elevates via UAC automatically inside the PowerShell script.
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "%~dp0sunshine-setup.ps1"