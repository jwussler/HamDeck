# HamDeck Version Bump: 2.0 -> 2.1
# Run from C:\Users\jwuss\HamDeck

# HamDeck.iss
(Get-Content HamDeck.iss) -replace 'MyAppVersion "2\.0\.0"', 'MyAppVersion "2.1.0"' `
    -replace 'HamDeck-v2\.0-Setup', 'HamDeck-v2.1-Setup' `
    -replace '; HamDeck v2\.0', '; HamDeck v2.1' |
    Set-Content HamDeck.iss

# HamDeck.csproj
(Get-Content HamDeck.csproj) -replace '<Version>2\.0\.0</Version>', '<Version>2.1.0</Version>' |
    Set-Content HamDeck.csproj

# MainWindow.xaml
(Get-Content Views\MainWindow.xaml) -replace 'HamDeck v2\.0', 'HamDeck v2.1' |
    Set-Content Views\MainWindow.xaml

# MainWindow.xaml.cs
(Get-Content Views\MainWindow.xaml.cs) -replace 'HamDeck v2\.0', 'HamDeck v2.1' |
    Set-Content Views\MainWindow.xaml.cs

# ApiServer.cs
(Get-Content Services\ApiServer.cs) -replace 'version = "2\.0"', 'version = "2.1"' |
    Set-Content Services\ApiServer.cs

# README.md
(Get-Content README.md) -replace 'HamDeck v2\.0', 'HamDeck v2.1' `
    -replace 'HamDeck-v2\.0-Setup', 'HamDeck-v2.1-Setup' `
    -replace 'HamDeck-v2\.0-dist', 'HamDeck-v2.1-dist' |
    Set-Content README.md

# build-dist.bat
(Get-Content build-dist.bat) -replace 'HamDeck v2\.0', 'HamDeck v2.1' `
    -replace 'HamDeck-v2\.0-dist', 'HamDeck-v2.1-dist' |
    Set-Content build-dist.bat

# wwwroot/index.html
(Get-Content wwwroot\index.html) -replace 'HamDeck v2\.0', 'HamDeck v2.1' |
    Set-Content wwwroot\index.html

# Also update the debug wwwroot copy
if (Test-Path bin\Debug\net8.0-windows\wwwroot\index.html) {
    (Get-Content bin\Debug\net8.0-windows\wwwroot\index.html) -replace 'HamDeck v2\.0', 'HamDeck v2.1' |
        Set-Content bin\Debug\net8.0-windows\wwwroot\index.html
}

Write-Host ""
Write-Host "Version bumped to 2.1.0 across all files." -ForegroundColor Green
Write-Host ""
Write-Host "Verify with:"
Write-Host '  findstr /s /r "v2\.0 2\.0\.0" *.cs *.xaml *.csproj *.iss *.bat *.md' -ForegroundColor Cyan
