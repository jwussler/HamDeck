# generate-icons.ps1 (fixed)
# Run from C:\Users\jwuss\HamDeck
# powershell -ExecutionPolicy Bypass -File .\generate-icons.ps1

Add-Type -AssemblyName System.Drawing

$outDir = "wwwroot\icons"
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

function Make-Icon {
    param([int]$size, [string]$filename, [bool]$maskable = $false)

    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

    # Background
    if ($maskable) {
        $g.Clear([System.Drawing.Color]::FromArgb(255, 17, 24, 32))
    } else {
        $g.Clear([System.Drawing.Color]::FromArgb(255, 10, 14, 20))
    }

    # Card rect — pre-compute all values
    $pad     = [int]($size * 0.08)
    $cardW   = $size - ($pad * 2)
    $cardH   = $size - ($pad * 2)
    $cardBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 17, 24, 32))
    $g.FillRectangle($cardBrush, $pad, $pad, $cardW, $cardH)

    # Accent bar at top
    $barH        = [int]($size * 0.04)
    $accentBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 45, 140, 240))
    $g.FillRectangle($accentBrush, $pad, $pad, $cardW, $barH)

    # "HD" text
    $fontSize = [int]($size * 0.32)
    $font     = New-Object System.Drawing.Font("Consolas", $fontSize, [System.Drawing.FontStyle]::Bold)
    $txtBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 45, 140, 240))
    $txt      = "HD"
    $txtSize  = $g.MeasureString($txt, $font)
    $tx       = ($size - $txtSize.Width)  / 2
    $ty       = ($size - $txtSize.Height) / 2 - [int]($size * 0.03)
    $g.DrawString($txt, $font, $txtBrush, $tx, $ty)

    # "WA0O" sub-label
    $subFontSize = [int]($size * 0.10)
    $subFont     = New-Object System.Drawing.Font("Consolas", $subFontSize, [System.Drawing.FontStyle]::Regular)
    $subBrush    = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 107, 125, 147))
    $sub         = "WA0O"
    $subSz       = $g.MeasureString($sub, $subFont)
    $sx          = ($size - $subSz.Width) / 2
    $sy          = $ty + $txtSize.Height - [int]($size * 0.04)
    $g.DrawString($sub, $subFont, $subBrush, $sx, $sy)

    $path = Join-Path $outDir $filename
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "  Created: $path" -ForegroundColor Green
}

Write-Host "Generating HamDeck PWA icons..." -ForegroundColor Cyan
Make-Icon -size 192 -filename "icon-192.png"
Make-Icon -size 512 -filename "icon-512.png"
Make-Icon -size 512 -filename "icon-maskable-512.png" -maskable $true

Write-Host "`nDone. Icons saved to $outDir" -ForegroundColor Green
