@echo off
rem DropLink portable launcher - the fallback when the root shortcut does not
rem start anything. It lives two folders down, hence %~dp0..\..\.
setlocal EnableExtensions
set "ROOT=%~dp0..\..\"
set "APP=%ROOT%app\DropLink.exe"
set "BACKEND=%ROOT%backend"
set "DRIVER=%ROOT%_backup\droplink-driver"

if not exist "%APP%" (
  echo DropLink.exe is missing: %APP%
  pause
  exit /b 1
)
if not exist "%BACKEND%\airbridge_radio.exe" (
  echo The backend folder is missing next to DropLink.
  pause
  exit /b 1
)

if not exist "%ROOT%data\received" mkdir "%ROOT%data\received"
if not exist "%ROOT%data\keys" mkdir "%ROOT%data\keys"
if not exist "%ROOT%data\logs" mkdir "%ROOT%data\logs"
if not exist "%ROOT%data\settings.json" copy /y "%ROOT%data\settings.default.json" "%ROOT%data\settings.json" >nul

set "DROPLINK_RADIO_EXE=%BACKEND%\airbridge_radio.exe"
set "DROPLINK_BRIDGE_EXE=%BACKEND%\radio_pipe_bridge.exe"
set "DROPLINK_NETD_EXE=%BACKEND%\awdl_netd.exe"
set "DROPLINK_AWDLD_EXE=%BACKEND%\awdld.exe"
set "DROPLINK_PYTHON=%BACKEND%\python\python.exe"
set "DROPLINK_ENGINE_SCRIPT=%BACKEND%\airdrop\engine.py"
set "DROPLINK_OUTPUT_DIR=%ROOT%data\received"
set "DROPLINK_KEYS_DIR=%ROOT%data\keys"
rem data\settings.json wins when present; this is only the fallback name.
set "DROPLINK_DEVICE_NAME=%COMPUTERNAME%"
set "DROPLINK_LOG_DIR=%ROOT%data\logs"
rem Auto-receive comes from data\settings.json. Setting it here as well would
rem override the settings page unconditionally, which is why turning it off in
rem the app used to do nothing while this launcher was the only entry point.
set "DROPLINK_SETUP_CMD=%DRIVER%\Switch-WinUSB.ps1"
set "DROPLINK_ROLLBACK_CMD=%DRIVER%\Restore-Driver.ps1"

rem The user authorised initialising this adapter and sending frames; a
rem one-click rollback ships next to the driver kit. Driver binding itself is
rem still a separate, explicitly confirmed step inside the app.
set "DROPLINK_AUTHORIZED=1"
set "DROPLINK_RECOVERY_VERIFIED=1"

rem Use wscript instead of "start": on managed machines a short-lived CMD job
rem can have its child process recycled, which makes the window never show.
set "VBS=%~dp0Start-DropLink.vbs"
if not exist "%VBS%" (
  echo Launcher missing: %VBS%
  pause
  exit /b 1
)
wscript.exe "%VBS%" %*
endlocal
