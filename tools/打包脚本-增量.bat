@echo off
setlocal

chcp 65001 >nul
cd /d "%~dp0.."

set "BASELINE_VERSION=0.0.100"
if not "%~2"=="" set "BASELINE_VERSION=%~2"
set "BASELINE_TAG=v%BASELINE_VERSION%"
set "BASELINE_APP_DIR=package\PackingProof+%BASELINE_TAG%\PackingProof+%BASELINE_TAG%\app"

set "VERSION_ARG="
if not "%~1"=="" set "VERSION_ARG=-Version %~1"

rem 双击（无版本参数）是临时出包：只编译/打包，不跑全量测试；带版本参数按完整发布验证执行。
set "TEST_ARGS="
if "%~1"=="" (
    set "TEST_ARGS=-SkipFullTest"
    echo [WARN] 未传版本号：本地快速打包，跳过全量测试；正式发布请带版本号运行或先跑 tools\Test-Release.ps1。
)

rem 第三个参数控制本地 7z：默认不生成，Release 也不再上传该产物。
rem 需要时传 7z，例如：打包脚本-增量.bat 0.0.67 0.0.66 7z
set "ARCHIVE_ARGS="
if /i "%~3"=="7z" set "ARCHIVE_ARGS=-IncludeSevenZip"
if /i "%~3"=="-IncludeSevenZip" set "ARCHIVE_ARGS=-IncludeSevenZip"

rem 默认包含 YOLO-OBB 功能包；传 novision 可显式生成不含视觉依赖的精简包。
set "VISION_ARGS="
if /i "%~3"=="vision" set "VISION_ARGS=-VisionObb include"
if /i "%~4"=="vision" set "VISION_ARGS=-VisionObb include"
rem novision 继续接受，便于旧命令兼容。
if /i "%~3"=="novision" set "VISION_ARGS=-VisionObb exclude"
if /i "%~4"=="novision" set "VISION_ARGS=-VisionObb exclude"

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
echo Launcher baseline: tools\launcher-baseline.json
if defined ARCHIVE_ARGS echo Local 7z archive: enabled
echo.

echo [WARN] Review docs\development\RELEASE_CHECKLIST.md before publishing.
echo [WARN] Unconfirmed real-device checks no longer block packaging.

rem 参数展开后拼成单行，避免 %ARCHIVE_ARGS% 为空时行尾 ^ 续接到空行导致命令被截断。
pwsh -NoProfile -ExecutionPolicy Bypass -File "tools\Publish-CleanPackage.ps1" %VERSION_ARG% %TEST_ARGS% -PatchBaselineVersion %BASELINE_VERSION% -BaselineAppDir "%BASELINE_APP_DIR%" %ARCHIVE_ARGS% %VISION_ARGS%

set "EXIT_CODE=%ERRORLEVEL%"
echo.
if "%EXIT_CODE%"=="0" (
    echo Package completed.
) else (
    echo Package failed. Exit code: %EXIT_CODE%
)
pause
exit /b %EXIT_CODE%
