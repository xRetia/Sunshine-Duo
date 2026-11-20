@echo off
setlocal enabledelayedexpansion

rem uninstall-sudovda.bat - remove SudoVDA virtual display driver (idempotent, no prompt)
rem Calls drivers\sudovda\nefconc.exe directly to avoid the pause in uninstall.bat.
rem Must stay pure ASCII: cmd.exe parses batch files in the OEM codepage.

rem Locate Sunshine root (parent of scripts)
for %%I in ("%~dp0\..") do set "ROOT_DIR=%%~fI"

set "SVDA_DIR=%ROOT_DIR%\drivers\sudovda"
set "NEFCON=%SVDA_DIR%\nefconc.exe"

if not exist "%NEFCON%" (
    echo [sudovda] ERROR: missing %NEFCON%
    exit /b 1
)

echo [sudovda] removing SudoVDA virtual display driver...
"%NEFCON%" --remove-device-node --hardware-id root\sudomaker\sudovda --class-guid "4D36E968-E325-11CE-BFC1-08002BE10318"
if %ERRORLEVEL% neq 0 (
    echo [sudovda] note: remove-device-node returned non-zero (device may not exist yet, ignored).
)

echo [sudovda] uninstall completed.
exit /b 0
