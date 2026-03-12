# add-pwa.ps1
# Injects PWA meta tags + SW registration into index.html, login.html, admin.html
# Run from C:\Users\jwuss\HamDeck after copying sw.js, manifest.json, icons/

$pwaHead = @'
    <link rel="manifest" href="/manifest.json">
    <meta name="theme-color" content="#2d8cf0">
    <meta name="mobile-web-app-capable" content="yes">
    <meta name="apple-mobile-web-app-capable" content="yes">
    <meta name="apple-mobile-web-app-status-bar-style" content="black-translucent">
    <meta name="apple-mobile-web-app-title" content="HamDeck">
    <link rel="apple-touch-icon" href="/icons/icon-192.png">
'@

$swScript = @'
    <script>
        if ('serviceWorker' in navigator) {
            window.addEventListener('load', () => {
                navigator.serviceWorker.register('/sw.js')
                    .catch(err => console.warn('SW registration failed:', err));
            });
        }
    </script>
'@

function Inject-PWA {
    param([string]$file)

    if (-not (Test-Path $file)) {
        Write-Host "  SKIP (not found): $file" -ForegroundColor Yellow
        return
    }

    $content = Get-Content $file -Raw

    # Inject head tags before </head> (only if not already present)
    if ($content -notmatch 'rel="manifest"') {
        $content = $content -replace '([ \t]*</head>)', "$pwaHead`$1"
        Write-Host "  Injected PWA head tags: $file" -ForegroundColor Green
    } else {
        Write-Host "  Already has manifest link, skipping head: $file" -ForegroundColor DarkGray
    }

    # Inject SW script before </body> (only if not already present)
    if ($content -notmatch 'serviceWorker') {
        $content = $content -replace '([ \t]*</body>)', "$swScript`$1"
        Write-Host "  Injected SW registration: $file" -ForegroundColor Green
    } else {
        Write-Host "  Already has SW registration, skipping: $file" -ForegroundColor DarkGray
    }

    Set-Content $file $content -NoNewline
}

Write-Host "Injecting PWA support into HTML files..." -ForegroundColor Cyan
Inject-PWA "wwwroot\index.html"
Inject-PWA "wwwroot\login.html"
Inject-PWA "wwwroot\admin.html"

# Also update bin\Debug copies if they exist
foreach ($f in @("index.html","login.html","admin.html")) {
    $debug = "bin\Debug\net8.0-windows\wwwroot\$f"
    if (Test-Path $debug) {
        Copy-Item "wwwroot\$f" $debug -Force
        Write-Host "  Updated debug copy: $debug" -ForegroundColor DarkCyan
    }
}

Write-Host "`nPWA injection complete." -ForegroundColor Green
