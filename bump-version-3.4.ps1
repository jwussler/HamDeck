# HamDeck Version Bump: 3.3 -> 3.4
# Run from C:\Users\jwuss\HamDeck

# HamDeck.iss
(Get-Content HamDeck.iss) `
    -replace 'MyAppVersion \"3\.3\.0\"', 'MyAppVersion "3.4.0"' `
    -replace 'HamDeck-v3\.3-Setup', 'HamDeck-v3.4-Setup' |
    Set-Content HamDeck.iss

# HamDeck.csproj
(Get-Content HamDeck.csproj) `
    -replace '<Version>3\.3\.0</Version>', '<Version>3.4.0</Version>' |
    Set-Content HamDeck.csproj

# Views\MainWindow.xaml  (was on 3.2, bring to 3.4)
(Get-Content Views\MainWindow.xaml) `
    -replace 'HamDeck v3\.[0-9]+', 'HamDeck v3.4' |
    Set-Content Views\MainWindow.xaml

# Views\MainWindow.xaml.cs
(Get-Content Views\MainWindow.xaml.cs) `
    -replace 'HamDeck v3\.[0-9]+', 'HamDeck v3.4' |
    Set-Content Views\MainWindow.xaml.cs

# Services\ApiServer.cs  (health endpoint version string)
(Get-Content Services\ApiServer.cs) `
    -replace 'version = \"3\.[0-9]+\"', 'version = "3.4"' |
    Set-Content Services\ApiServer.cs

# wwwroot\index.html
(Get-Content wwwroot\index.html) `
    -replace 'HamDeck v3\.[0-9]+', 'HamDeck v3.4' `
    -replace '"v3\.[0-9]+"', '"v3.4"' `
    -replace '>v3\.[0-9]+<', '>v3.4<' |
    Set-Content wwwroot\index.html

# Sync index.html to bin\Debug wwwroot (the MSBuild target does this on build,
# but running it here means the file is ready before the next Ctrl+Shift+B)
if (Test-Path bin\Debug\net8.0-windows\wwwroot\index.html) {
    (Get-Content bin\Debug\net8.0-windows\wwwroot\index.html) `
        -replace 'HamDeck v3\.[0-9]+', 'HamDeck v3.4' `
        -replace '"v3\.[0-9]+"', '"v3.4"' `
        -replace '>v3\.[0-9]+<', '>v3.4<' |
        Set-Content bin\Debug\net8.0-windows\wwwroot\index.html
}

Write-Host ""
Write-Host "Version bumped to 3.4.0 across all files." -ForegroundColor Green
Write-Host ""
Write-Host "Verify with:"
Write-Host '  findstr /s "3\.3" *.cs *.xaml *.csproj *.iss *.html' -ForegroundColor Cyan
Write-Host ""
Write-Host "Then commit:"
Write-Host '  git add -A && git commit -m "chore: bump version to 3.4.0"' -ForegroundColor Cyan
