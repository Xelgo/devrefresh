@echo off
setlocal EnableExtensions DisableDelayedExpansion
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Update-Online.ps1" %*
set "taskExit=%ERRORLEVEL%"
if "%~1"=="" pause
exit /b %taskExit%
