@echo off
setlocal
cd /d "%~dp0"
if not exist "src\SteamControllerRefreshRate\bin\Release\net10.0\sc-refresh.exe" (
    dotnet build SteamControllerRefreshRate.sln -c Release --nologo -v quiet
)
"src\SteamControllerRefreshRate\bin\Release\net10.0\sc-refresh.exe" disable-persistent --restart-steam
echo.
"src\SteamControllerRefreshRate\bin\Release\net10.0\sc-refresh.exe" status
echo.
pause
