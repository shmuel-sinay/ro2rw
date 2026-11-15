@echo off
rem RO2RW Manager launcher
cd /d "%~dp0"
start "" powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "%~dp0RO2RW-Manager.ps1"
