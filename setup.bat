@echo off
rem MoeArt Duo one-click setup launcher (no console window).
rem Real work is done by setup.exe next to this file; its app manifest
rem (requireAdministrator) makes Windows ask for UAC elevation regardless
rem of how this script was started (double click, task, command line).
if exist "%~dp0setup.exe" (
  start "" /min "%~dp0setup.exe"
) else (
  start "" /min "%~dp0bin\SunshineDuoSetup\SunshineDuoSetup.exe"
)
exit
