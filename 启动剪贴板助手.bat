@echo off
REM Launcher: check .NET runtime via PowerShell, then start app
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0launcher.ps1"
if %ERRORLEVEL% NEQ 0 pause
