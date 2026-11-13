@echo off
setlocal EnableExtensions DisableDelayedExpansion
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Update.ps1" -Rollback
set "taskExit=%ERRORLEVEL%"
pause
exit /b %taskExit%
