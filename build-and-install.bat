@echo off
echo ============================================
echo   HamDeck v2.0 - Build and Install
echo ============================================
echo.

:: Build
echo [1/2] Building Release...
dotnet build -c Release -v q

if not exist "bin\Release\net8.0-windows\HamDeck.exe" (
    echo.
    echo ERROR: Build failed!
    pause
    exit /b 1
)

:: Create desktop shortcut
echo [2/2] Creating desktop shortcut...
set "EXEPATH=%CD%\bin\Release\net8.0-windows\HamDeck.exe"
set "WORKDIR=%CD%\bin\Release\net8.0-windows"
set "SHORTCUT=%USERPROFILE%\Desktop\HamDeck.lnk"
if exist "%USERPROFILE%\OneDrive\Desktop" set "SHORTCUT=%USERPROFILE%\OneDrive\Desktop\HamDeck.lnk"

powershell -Command "$ws = New-Object -ComObject WScript.Shell; $sc = $ws.CreateShortcut('%SHORTCUT%'); $sc.TargetPath = '%EXEPATH%'; $sc.WorkingDirectory = '%WORKDIR%'; $sc.Description = 'HamDeck v2.0'; $sc.Save()"

echo.
echo ============================================
echo   Done! Desktop shortcut created.
echo   EXE: %EXEPATH%
echo ============================================
echo.
pause
