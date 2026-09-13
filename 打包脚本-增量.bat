@echo off
setlocal

chcp 65001 >nul
cd /d "%~dp0"

set "BASELINE_VERSION=0.0.18"
if not "%~2"=="" set "BASELINE_VERSION=%~2"
set "BASELINE_TAG=v%BASELINE_VERSION%"
set "BASELINE_PACKAGE_DIR=package\PackingProof+%BASELINE_TAG%"
set "BASELINE_FULL_DIR=%BASELINE_PACKAGE_DIR%\PackingProof+%BASELINE_TAG%"
set "BASELINE_APP_DIR=%BASELINE_FULL_DIR%\app"
if not exist "%BASELINE_APP_DIR%\ExpressPackingMonitoring.exe" (
    set "BASELINE_PACKAGE_DIR=package\ExpressPackingMonitoring+%BASELINE_TAG%"
    set "BASELINE_FULL_DIR=package\ExpressPackingMonitoring+%BASELINE_TAG%\ExpressPackingMonitoring+%BASELINE_TAG%"
    set "BASELINE_APP_DIR=package\ExpressPackingMonitoring+%BASELINE_TAG%\ExpressPackingMonitoring+%BASELINE_TAG%\app"
)

set "VERSION_ARG="
if not "%~1"=="" set "VERSION_ARG=-Version %~1"

rem 第三个参数控制本地 7z：默认不生成，Release 也不再上传该产物。
rem 需要时传 7z，例如：打包脚本-增量.bat 0.0.67 0.0.66 7z
set "ARCHIVE_ARGS="
if /i "%~3"=="7z" set "ARCHIVE_ARGS=-IncludeSevenZip"
if /i "%~3"=="-IncludeSevenZip" set "ARCHIVE_ARGS=-IncludeSevenZip"

if not exist "%BASELINE_APP_DIR%\ExpressPackingMonitoring.exe" (
    echo [ERROR] Baseline app not found:
    echo         %BASELINE_APP_DIR%
    echo.
    echo Put the v%BASELINE_VERSION% full package app directory here first, then run this script again.
    echo Expected:
    echo         %BASELINE_APP_DIR%\ExpressPackingMonitoring.exe
    echo.
    pause
    exit /b 1
)

echo Baseline version: %BASELINE_VERSION%
echo Baseline app:     %BASELINE_APP_DIR%
echo Launcher baseline: Tools\launcher-baseline.json
if defined ARCHIVE_ARGS echo Local 7z archive: enabled
echo.

echo [WARN] Review RELEASE_CHECKLIST.md before publishing.
echo [WARN] Unconfirmed real-device checks no longer block packaging.

rem 参数展开后拼成单行，避免 %ARCHIVE_ARGS% 为空时行尾 ^ 续接到空行导致命令被截断。
pwsh -NoProfile -ExecutionPolicy Bypass -File "Tools\Publish-CleanPackage.ps1" %VERSION_ARG% -PatchBaselineVersion %BASELINE_VERSION% -BaselineAppDir "%BASELINE_APP_DIR%" %ARCHIVE_ARGS%

set "EXIT_CODE=%ERRORLEVEL%"
echo.
if "%EXIT_CODE%"=="0" (
    echo Package completed.
) else (
    echo Package failed. Exit code: %EXIT_CODE%
)
pause
exit /b %EXIT_CODE%
