@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\Build-OpenPuckFirmware.ps1"
echo.
pause
