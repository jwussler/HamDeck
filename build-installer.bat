@echo off
echo ============================================
echo   HamDeck v2.0 - Build Installer
echo ============================================
echo.

echo [1/3] Cleaning...
dotnet clean -v q

echo [2/3] Publishing self-contained release...
dotnet publish -c Release -r win-x64 --self-contained true -v q

set "PUBDIR=bin\Release\net8.0-windows\win-x64\publish"
if not exist "%PUBDIR%\HamDeck.exe" (
    echo ERROR: Build failed!
    pause
    exit /b 1
)

echo [3/3] Building installer...
if not exist "installer" mkdir installer

:: Try common Inno Setup locations
set "ISCC="
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"

if "%ISCC%"=="" (
    echo.
    echo Inno Setup not found! Install it from:
    echo https://jrsoftware.org/isdl.php
    echo.
    echo Then run this script again.
    pause
    exit /b 1
)

"%ISCC%" HamDeck.iss

echo.
echo ============================================
echo   Installer created:
echo   %CD%\installer\HamDeck-v2.0-Setup.exe
echo ============================================
echo.
pause
