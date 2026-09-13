@echo off
setlocal

chcp 65001 >nul
cd /d "%~dp0"

rem 默认不再生成 7z：Release 不上传该产物，生成它只是白白多花打包时间。
rem 本地确有需要时传 7z 或 -IncludeSevenZip，例如：打包脚本-基线.bat 7z
set "ARCHIVE_ARGS="
if /i "%~1"=="7z" set "ARCHIVE_ARGS=-IncludeSevenZip"
if /i "%~1"=="-IncludeSevenZip" set "ARCHIVE_ARGS=-IncludeSevenZip"
if defined ARCHIVE_ARGS echo [INFO] Local 7z archive enabled.

echo [WARN] Review RELEASE_CHECKLIST.md before publishing.
echo [WARN] Unconfirmed real-device checks no longer block packaging.

pwsh -NoProfile -ExecutionPolicy Bypass -File "Tools\Publish-CleanPackage.ps1" -DisablePatch %ARCHIVE_ARGS%
exit /b %ERRORLEVEL%
