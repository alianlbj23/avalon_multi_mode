@echo off
setlocal
cd /d "%~dp0"
title TGCoop Installer

if not exist "%~dp0install_tgcoop.ps1" (
    echo [X] install_tgcoop.ps1 not found next to this file.
    echo     Please EXTRACT the whole ZIP to a folder first, then run this file
    echo     from inside the extracted folder. Do not run it inside the ZIP window.
    echo.
    pause
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install_tgcoop.ps1" %*
set "RC=%ERRORLEVEL%"

if not "%RC%"=="0" (
    echo.
    echo [X] Installer exited with error code %RC%.
    echo     Please send the file install_log.txt, which is next to this file, to your friend.
    echo.
    pause
)
exit /b %RC%
