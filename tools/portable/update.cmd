@echo off
setlocal
cd /d "%~dp0"

rem Self-elevate: the running DropLink is elevated, so only an elevated
rem shell can terminate it and overwrite app\DropLink.exe.
net session >nul 2>&1
if errorlevel 1 (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

if not exist "app\DropLink.new.exe" (
    echo No newer build in app\DropLink.new.exe - starting the installed one.
    start "" "app\DropLink.exe"
    ping -n 3 127.0.0.1 >nul
    exit /b 0
)

echo Stopping DropLink and its backends...
taskkill /F /IM DropLink.exe >nul 2>&1
taskkill /F /IM airbridge_radio.exe >nul 2>&1
taskkill /F /IM radio_pipe_bridge.exe >nul 2>&1
taskkill /F /IM awdl_netd.exe >nul 2>&1
taskkill /F /IM awdld.exe >nul 2>&1
rem The adapter needs a few seconds after its last handle goes away. Restarting
rem the radio seconds later leaves it at rtw_read: LIBUSB_ERROR_IO and it hangs
rem until the retry budget runs out.
ping -n 12 127.0.0.1 >nul

echo Installing new build...
copy /Y "app\DropLink.new.exe" "app\DropLink.exe" >nul
if errorlevel 1 (
    echo [X] Could not replace app\DropLink.exe. Close DropLink and run this again.
    ping -n 6 127.0.0.1 >nul
    exit /b 1
)
del /Q "app\DropLink.new.exe" >nul 2>&1

echo Starting DropLink...
start "" "app\DropLink.exe"
echo Done. This window closes itself.
ping -n 3 127.0.0.1 >nul
exit /b 0
