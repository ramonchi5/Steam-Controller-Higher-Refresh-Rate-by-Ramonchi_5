@echo off
setlocal
cd /d "%~dp0"
if exist "src\SteamControllerRefreshRate\bin\Release\net10.0\sc-refresh.exe" (
    "src\SteamControllerRefreshRate\bin\Release\net10.0\sc-refresh.exe" stop-boost >nul 2>&1
)
echo Building Steam Controller Higher-Refresh-Rate tools...
dotnet build SteamControllerRefreshRate.sln -c Release --nologo -v quiet
if errorlevel 1 (
    echo Build failed!
    pause
    exit /b 1
)
"src\SteamControllerRefreshRate\bin\Release\net10.0\sc-refresh.exe" enable-persistent --restart-steam
echo.
"src\SteamControllerRefreshRate\bin\Release\net10.0\sc-refresh.exe" status
echo.
pause
