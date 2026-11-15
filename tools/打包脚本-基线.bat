@echo off
setlocal

chcp 65001 >nul
cd /d "%~dp0.."

rem 默认不再生成 7z：Release 不上传该产物，生成它只是白白多花打包时间。
rem 本地确有需要时传 7z 或 -IncludeSevenZip，例如：打包脚本-基线.bat 7z
set "ARCHIVE_ARGS="
if /i "%~1"=="7z" set "ARCHIVE_ARGS=-IncludeSevenZip"
if /i "%~1"=="-IncludeSevenZip" set "ARCHIVE_ARGS=-IncludeSevenZip"
if defined ARCHIVE_ARGS echo [INFO] Local 7z archive enabled.

set "VISION_ARGS="
if /i "%~1"=="vision" set "VISION_ARGS=-VisionObb include"
if /i "%~2"=="vision" set "VISION_ARGS=-VisionObb include"
rem 默认包含视觉功能包；novision 可显式生成不含视觉依赖的精简包。
if /i "%~1"=="novision" set "VISION_ARGS=-VisionObb exclude"
if /i "%~2"=="novision" set "VISION_ARGS=-VisionObb exclude"

rem 双击（无参数）是临时出包：只编译/打包，不跑全量测试；带参数运行按发布验证执行。
set "TEST_ARGS="
if "%~1"=="" (
    set "TEST_ARGS=-SkipFullTest"
    echo [WARN] 未传参数：本地快速打包，跳过全量测试；正式发布请先跑 tools\Test-Release.ps1。
)

echo [WARN] Review docs\development\RELEASE_CHECKLIST.md before publishing.
echo [WARN] Unconfirmed real-device checks no longer block packaging.

pwsh -NoProfile -ExecutionPolicy Bypass -File "tools\Publish-CleanPackage.ps1" %TEST_ARGS% -DisablePatch %ARCHIVE_ARGS% %VISION_ARGS%
exit /b %ERRORLEVEL%
