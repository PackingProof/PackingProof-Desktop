@echo off
setlocal

ver | findstr /r /c:"6\.1\." >nul
if errorlevel 1 (
    chcp 65001 >nul
)
title PackingProof Launcher Update
cd /d "%~dp0"

where powershell.exe >nul 2>nul
if errorlevel 1 (
    echo Windows PowerShell is required to install this update.
    echo.
    pause
    exit /b 1
)

set "EPM_LAUNCHER_PATCH_ROOT=%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$root=$env:EPM_LAUNCHER_PATCH_ROOT; $scriptPath=Join-Path $root 'apply_launcher_patch.ps1'; $scriptText=[System.IO.File]::ReadAllText($scriptPath,[System.Text.Encoding]::UTF8); & ([ScriptBlock]::Create($scriptText)) -PatchRoot $root"
set "EXIT_CODE=%ERRORLEVEL%"

echo.
pause
exit /b %EXIT_CODE%
