@echo off
setlocal EnableExtensions
title DropLink - restore original driver
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Restore-Driver.ps1"
echo.
echo Exit code: %ERRORLEVEL%
timeout /t 12 >nul
exit /b %ERRORLEVEL%
