@echo off
setlocal

chcp 65001 >nul
cd /d "%~dp0"

echo [WARN] Review RELEASE_CHECKLIST.md before publishing.
echo [WARN] Unconfirmed real-device checks no longer block packaging.

pwsh -NoProfile -ExecutionPolicy Bypass -File "Tools\Publish-CleanPackage.ps1" -DisablePatch
exit /b %ERRORLEVEL%
