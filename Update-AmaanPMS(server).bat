@echo off
COLOR 0A
echo ========================================
echo   AMAAN PMS - SERVICE UPDATE SCRIPT
echo ========================================
echo.

REM Get project path
set PROJECT_PATH=C:\SERVER NEXTGEN SOFTWARE\AmaanParkingSystem\AmaanParkingSystem
set PUBLISH_PATH=C:\AmaanPMS

echo [1/5] Stopping service...
net stop AmaanPMS 2>nul
timeout /t 3 /nobreak >nul

echo [2/5] Killing any running processes...
taskkill /F /IM AmaanParkingSystem.exe 2>nul
taskkill /F /IM dotnet.exe 2>nul
timeout /t 2 /nobreak >nul

echo [3/5] Publishing new version...
cd /d %PROJECT_PATH%
dotnet publish -c Release -o %PUBLISH_PATH% --force --no-cache

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo ERROR: Publishing failed!
    pause
    exit /b 1
)

echo [4/5] Starting service...
net start AmaanPMS

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo ERROR: Service failed to start!
    echo Check Event Viewer for details.
    pause
    exit /b 1
)

echo [5/5] Checking status...
sc query AmaanPMS | find "RUNNING"

if %ERRORLEVEL% EQU 0 (
    echo.
    COLOR 0B
    echo ========================================
    echo   SUCCESS! Service is running.
    echo   Open browser: http://localhost:5000
    echo   Press Ctrl+F5 to refresh cache
    echo ========================================
) else (
    echo.
    COLOR 0C
    echo ========================================
    echo   WARNING: Service may not be running
    echo   Check: services.msc
    echo ========================================
)

echo.
pause
