# HamDeck version bump - parameterized and encoding-safe.
#
# Usage:  powershell -ExecutionPolicy Bypass -File bump-version.ps1 -NewVersion 3.4.3
#
# Detects the current version from HamDeck.csproj (single source of truth) and
# updates the canonical version locations. Reads/writes UTF-8 and PRESERVES each
# file's original BOM state, so it will not corrupt the em-dashes/emoji in
# MainWindow.xaml etc. (Do NOT use Get-Content/Set-Content for this: on Windows
# PowerShell 5.1 Set-Content writes ANSI and mangles those characters.)

param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$NewVersion
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# --- detect current version from the .csproj ---
$csprojPath = Join-Path $root 'HamDeck.csproj'
$csproj = [IO.File]::ReadAllText($csprojPath, $utf8NoBom)
if ($csproj -notmatch '<Version>(\d+\.\d+\.\d+)</Version>') {
    throw "Could not find <Version>x.y.z</Version> in HamDeck.csproj"
}
$old = $Matches[1]
if ($old -eq $NewVersion) {
    Write-Host "Already at $NewVersion - nothing to do." -ForegroundColor Yellow
    return
}
Write-Host "Bumping $old -> $NewVersion" -ForegroundColor Cyan

# --- encoding-preserving literal replace across a file ---
function Update-File([string]$rel, [string[]]$olds, [string[]]$news) {
    $path = Join-Path $root $rel
    if (-not (Test-Path $path)) { Write-Host ("  skip (missing): {0}" -f $rel); return }
    $bytes  = [IO.File]::ReadAllBytes($path)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text   = [IO.File]::ReadAllText($path, $utf8NoBom)   # ReadAllText drops a leading BOM if present
    $total  = 0
    for ($i = 0; $i -lt $olds.Length; $i++) {
        $cnt = ([regex]::Matches($text, [regex]::Escape($olds[$i]))).Count
        if ($cnt -gt 0) { $text = $text.Replace($olds[$i], $news[$i]); $total += $cnt }
    }
    $enc = New-Object System.Text.UTF8Encoding($hasBom)   # preserve original BOM state
    [IO.File]::WriteAllText($path, $text, $enc)
    Write-Host ("  {0,-26} {1} replacement(s)" -f $rel, $total)
}

Update-File 'HamDeck.csproj'           @("<Version>$old</Version>")    @("<Version>$NewVersion</Version>")
Update-File 'HamDeck.iss'              @("MyAppVersion `"$old`"")      @("MyAppVersion `"$NewVersion`"")
Update-File 'Views\MainWindow.xaml'    @("HamDeck v$old")              @("HamDeck v$NewVersion")
Update-File 'Views\MainWindow.xaml.cs' @("HamDeck v$old")              @("HamDeck v$NewVersion")

Write-Host ""
Write-Host "Version bumped to $NewVersion. Review with: git diff --stat" -ForegroundColor Green
Write-Host "(Dashboard wwwroot/index.html follows its own version label and is left untouched.)"
