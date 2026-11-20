@echo off
setlocal enabledelayedexpansion

rem install-sudovda.bat - install SudoVDA virtual display driver (idempotent)
rem Wraps drivers\sudovda\install.bat. Must stay pure ASCII: cmd.exe parses
rem batch files in the OEM codepage (GBK on zh-CN), and UTF-8 Chinese text
rem corrupts parsing (commands get skipped while the script still exits 0).

rem Locate Sunshine root (parent of scripts)
for %%I in ("%~dp0\..") do set "ROOT_DIR=%%~fI"

set "SVDA_DIR=%ROOT_DIR%\drivers\sudovda"
set "SVDA_INSTALL_BAT=%SVDA_DIR%\install.bat"

if not exist "%SVDA_INSTALL_BAT%" (
    echo [sudovda] ERROR: missing %SVDA_INSTALL_BAT%
    exit /b 1
)

if not exist "%SVDA_DIR%\nefconc.exe" (
    echo [sudovda] ERROR: missing %SVDA_DIR%\nefconc.exe
    exit /b 1
)

echo [sudovda] installing SudoVDA virtual display driver...

rem certutil certs + nefconc device node + driver install
"%SVDA_INSTALL_BAT%"
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" (
    echo [sudovda] ERROR: drivers\sudovda\install.bat exit code %RC% (1=cert, 2=devnode, 3=driver)
    exit /b %RC%
)

echo [sudovda] install completed.
exit /b 0
