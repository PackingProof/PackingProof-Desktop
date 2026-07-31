@echo off
setlocal

chcp 65001 >nul
title PackingProof - 更新启动器
cd /d "%~dp0"

echo PackingProof 启动器更新
echo.
echo 请先完整解压 LauncherPatch，再双击此脚本。
echo 此脚本只更新软件根目录入口，不会修改录像、配置、数据库或主程序文件。
echo 如果无法自动找到安装位置，会提示拖入原软件文件夹或根目录启动器。
echo 请勿单独移动此 CMD、apply_launcher_patch.ps1、launcher_patch_manifest.json 或启动器 EXE。
echo.

where powershell.exe >nul 2>nul
if errorlevel 1 (
    echo [错误] 未找到 Windows PowerShell，无法安装启动器更新。
    echo.
    pause
    exit /b 1
)

set "EPM_LAUNCHER_PATCH_ROOT=%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$root=$env:EPM_LAUNCHER_PATCH_ROOT; $scriptPath=Join-Path $root 'apply_launcher_patch.ps1'; $scriptText=[System.IO.File]::ReadAllText($scriptPath,[System.Text.Encoding]::UTF8); & ([ScriptBlock]::Create($scriptText)) -PatchRoot $root"
set "EXIT_CODE=%ERRORLEVEL%"

echo.
if "%EXIT_CODE%"=="0" (
    echo 启动器更新已完成，下次从原入口启动软件即可。
) else (
    echo 启动器更新失败，请根据上方提示处理后重试。
)
echo.
pause
exit /b %EXIT_CODE%
