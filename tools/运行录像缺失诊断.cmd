@echo off
setlocal EnableExtensions
set "PWSH_EXE="
call :FindPwsh
if not defined PWSH_EXE call :InstallPwsh
if not defined PWSH_EXE goto :PwshUnavailable

"%PWSH_EXE%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Diagnose-MissingVideos.ps1" -OutputDir "%~dp0diagnostic-report"
if errorlevel 1 goto :DiagnosticFailed
echo.
echo Diagnostic completed. See: %~dp0diagnostic-report
echo Please send this folder back. Do not send recordings or the whole database.
pause
exit /b 0

:FindPwsh
for /f "delims=" %%I in ('where pwsh.exe 2^>nul') do (
  set "PWSH_EXE=%%I"
  exit /b 0
)
if exist "%LOCALAPPDATA%\Microsoft\WindowsApps\pwsh.exe" (
  set "PWSH_EXE=%LOCALAPPDATA%\Microsoft\WindowsApps\pwsh.exe"
  exit /b 0
)
if exist "%ProgramFiles%\PowerShell\7\pwsh.exe" (
  set "PWSH_EXE=%ProgramFiles%\PowerShell\7\pwsh.exe"
  exit /b 0
)
if exist "%ProgramFiles%\PowerShell\7-preview\pwsh.exe" (
  set "PWSH_EXE=%ProgramFiles%\PowerShell\7-preview\pwsh.exe"
  exit /b 0
)
exit /b 1

:InstallPwsh
echo PowerShell 7 was not found. Installing it automatically...
where winget.exe >nul 2>nul
if errorlevel 1 exit /b 1
winget.exe install --id Microsoft.PowerShell --source winget --silent --disable-interactivity --accept-source-agreements --accept-package-agreements
if errorlevel 1 exit /b 1
echo PowerShell 7 installation completed. Continuing diagnostics...
call :FindPwsh
exit /b 0

:PwshUnavailable
echo.
echo PowerShell 7 could not be installed automatically.
echo Send videos.db and the log folder for offline diagnosis.
pause
exit /b 1

:DiagnosticFailed
echo.
echo Diagnostic failed. Read the message above before sending files.
pause
exit /b 1
