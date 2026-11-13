@echo off
setlocal EnableExtensions DisableDelayedExpansion
chcp 65001 >nul
cd /d "%~dp0"
if not exist "%~dp0logs" mkdir "%~dp0logs"
if not exist "%~dp0app\DevRefresh.exe" (
  echo ERROR: app\DevRefresh.exe not found. Extract the complete ZIP first.
  if /i not "%~1"=="--no-pause" pause
  exit /b 1
)
echo DevRefresh - starting...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Show-Endpoints.ps1"
if errorlevel 1 (
  if /i not "%~1"=="--no-pause" pause
  exit /b 1
)
echo Keep this window open. Press Ctrl+C to stop.
echo Logs: %~dp0logs
echo If startup fails, run CHECK.cmd and COLLECT-LOGS.cmd.
"%~dp0app\DevRefresh.exe" --contentRoot "%~dp0app" --ServerConfiguration "%~dp0config\server.json" >> "%~dp0logs\launcher.log" 2>&1
set "taskExit=%ERRORLEVEL%"
echo.
echo DevRefresh stopped. Exit code: %taskExit%
if not "%taskExit%"=="0" (
  echo Last launcher messages:
  set "DEVREFRESH_LAUNCHER_LOG=%~dp0logs\launcher.log"
  powershell.exe -NoProfile -Command "Get-Content -LiteralPath $env:DEVREFRESH_LAUNCHER_LOG -Tail 20 -Encoding UTF8"
)
if /i not "%~1"=="--no-pause" pause
exit /b %taskExit%
