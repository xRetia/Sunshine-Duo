@echo off
rem Sunshine one-click setup launcher (auto UAC elevation, no visible console)
rem Real logic: scripts\sunshine-setup.ps1 (all paths relative, portable)
start "" /min powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "%~dp0scripts\sunshine-setup.ps1"
exit
