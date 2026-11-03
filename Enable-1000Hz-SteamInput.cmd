@echo off
setlocal
cd /d "%~dp0"
if not exist "src\SteamControllerRefreshRate\bin\Release\net10.0\sc-refresh.exe" (
    dotnet build SteamControllerRefreshRate.sln -c Release --nologo -v quiet
)
"src\SteamControllerRefreshRate\bin\Release\net10.0\sc-refresh.exe" set-steam-rate 1000 --restart-steam
echo.
pause
