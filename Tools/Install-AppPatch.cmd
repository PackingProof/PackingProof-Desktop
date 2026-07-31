@echo off
setlocal

chcp 65001 >nul
title PackingProof - 安装主程序增量更新
cd /d "%~dp0"

echo PackingProof 主程序增量更新
echo.
echo 请先完整解压 AppPatch，再双击此脚本。
echo 脚本会校验全部补丁文件，安全关闭程序并直接完成更新。
echo 如果无法自动找到安装位置，会提示拖入原软件文件夹或程序入口。
echo 请勿单独移动此 CMD、apply_app_patch.ps1、patch_manifest.json 或 files 文件夹。
echo.

where powershell.exe >nul 2>nul
if errorlevel 1 (
    echo [错误] 未找到 Windows PowerShell，无法安装主程序更新。
    echo.
    pause
    exit /b 1
)

set "EPM_APP_PATCH_ROOT=%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$root=$env:EPM_APP_PATCH_ROOT; $scriptPath=Join-Path $root 'apply_app_patch.ps1'; $scriptText=[System.IO.File]::ReadAllText($scriptPath,[System.Text.Encoding]::UTF8); & ([ScriptBlock]::Create($scriptText)) -PatchRoot $root"
set "EXIT_CODE=%ERRORLEVEL%"

echo.
if "%EXIT_CODE%"=="0" (
    echo 主程序增量更新已完成。
) else (
    echo 主程序更新失败，请根据上方提示处理后重试。
)
echo.
pause
exit /b %EXIT_CODE%
