@echo off
rem Open the settings page inside the app itself. It used to run
rem settings-gui.ps1, which put a PowerShell console on screen behind the form.
rem DropLink is single-instance, so this reaches the app that is already running.
start "" "%~dp0..\..\app\DropLink.exe" --settings
