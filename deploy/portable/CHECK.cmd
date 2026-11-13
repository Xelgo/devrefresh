@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Check.ps1"
set "taskExit=%ERRORLEVEL%"
if /i not "%~1"=="--no-pause" pause
exit /b %taskExit%
