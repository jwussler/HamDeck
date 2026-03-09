@echo off
echo ============================================
echo   HamDeck v2.1 - Build Distribution Package
echo ============================================
echo.

echo [1/3] Cleaning...
dotnet clean -v q

echo [2/3] Building self-contained release...
dotnet publish -c Release -r win-x64 --self-contained true -v q

set "PUBDIR=bin\Release\net8.0-windows\win-x64\publish"

if not exist "%PUBDIR%\HamDeck.exe" (
    echo ERROR: Build failed!
    pause
    exit /b 1
)

echo [3/3] Creating distribution zip...
:: Remove old zip if exists
if exist "HamDeck-v2.1-dist.zip" del "HamDeck-v2.1-dist.zip"

:: Use PowerShell to create zip
powershell -Command "Compress-Archive -Path '%PUBDIR%\*' -DestinationPath 'HamDeck-v2.1-dist.zip' -Force"

echo.
echo ============================================
echo   Distribution package created:
echo   %CD%\HamDeck-v2.1-dist.zip
echo.
echo   Instructions for friends:
echo   1. Right-click the .zip, Properties, check Unblock, OK
echo   2. Extract to any folder
echo   3. Run HamDeck.exe
echo   4. No .NET install required
echo ============================================
echo.
pause
