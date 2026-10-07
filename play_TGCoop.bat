@echo off
setlocal
cd /d "%~dp0"
title TGCoop Launcher

if not exist "%~dp0launch_tgcoop.ps1" (
    echo [X] launch_tgcoop.ps1 not found next to this file.
    echo     Please EXTRACT the whole ZIP to a folder first and run this file from there.
    echo.
    pause
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0launch_tgcoop.ps1" %*
set "RC=%ERRORLEVEL%"

if not "%RC%"=="0" (
    echo.
    echo [X] Launcher exited with error code %RC%. See launcher_log.txt next to this file.
    echo.
    pause
)
exit /b %RC%
