@echo off
rem Fill in real-device benchmark data and write JSON + Markdown reports.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\..\backend\airdrop\benchmark.ps1"
