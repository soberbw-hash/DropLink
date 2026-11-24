@echo off
setlocal EnableExtensions
title DropLink - switch adapter to WinUSB
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Switch-WinUSB.ps1"
echo.
echo Exit code: %ERRORLEVEL%
timeout /t 5 >nul
exit /b %ERRORLEVEL%
