@echo off
setlocal EnableExtensions DisableDelayedExpansion
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Start-Service.ps1"
set "taskExit=%ERRORLEVEL%"
if /i not "%~1"=="--no-pause" pause
exit /b %taskExit%
