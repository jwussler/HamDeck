# HamDeck API Auth Test
# Run from the shack PC: powershell -ExecutionPolicy Bypass -File test-api-auth.ps1
# Tests that all endpoints enforce auth correctly.

$BASE = "https://radio.wa0o.com"   # change to https://radio.wa0o.com to test remotely

$pass = 0
$fail = 0

function Test-Endpoint {
    param($route, $expectedStatus, $label = "")
    $url = "$BASE$route"
    $tag = if ($label) { $label } else { $route }
    try {
        $resp = Invoke-WebRequest -Uri $url -Method GET -UseBasicParsing `
                    -ErrorAction SilentlyContinue
        $code = $resp.StatusCode
    } catch {
        # Invoke-WebRequest throws on 4xx/5xx in older PS - catch and extract code
        $code = $_.Exception.Response.StatusCode.value__
        if (-not $code) { $code = 0 }
    }
    $ok = ($code -eq $expectedStatus)
    $icon = if ($ok) { "[PASS]" } else { "[FAIL]" }
    $color = if ($ok) { "Green" } else { "Red" }
    Write-Host ("  {0} [{1,3}] {2}" -f $icon, $code, $tag) -ForegroundColor $color
    if ($ok) { $script:pass++ } else { $script:fail++ }
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  HamDeck API Auth Test" -ForegroundColor Cyan
Write-Host "  Target: $BASE" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

# -- PUBLIC - should return 200 without auth -----------------------------------
Write-Host "`nPUBLIC (expect 200 - no auth needed):" -ForegroundColor Yellow
@(
    "/api/health",
    "/api/status",
    "/api/status/full",
    "/api/meters",
    "/api/session",
    "/api/cluster/spots",
    "/api/record/status",
    "/api/freq",
    "/api/freq/get",
    "/api/volume/get",
    "/api/cw-speed/get",
    "/api/ant/get",
    "/api/ant/rx/get",
    "/api/auth/status",
    "/api/power/limit",
    "/api/vfo-lock/status"
) | ForEach-Object { Test-Endpoint $_ 200 }

# -- PROTECTED - should return 401 without auth --------------------------------
Write-Host "`nPROTECTED (expect 401 - auth required):" -ForegroundColor Yellow
@(
    "/api/ptt/on",
    "/api/ptt/off",
    "/api/ptt/toggle",
    "/api/mode/usb",
    "/api/mode/lsb",
    "/api/mode/cw",
    "/api/vfo/swap",
    "/api/vfo/a",
    "/api/vfo/b",
    "/api/vfo-copy/a2b",
    "/api/split/toggle",
    "/api/split/on",
    "/api/split/off",
    "/api/quick-split",
    "/api/freq/set/14200000",
    "/api/freq/clear",
    "/api/freq/send",
    "/api/band/40",
    "/api/preset/40ssb",
    "/api/step/100/up",
    "/api/power/high",
    "/api/power/qrp",
    "/api/power/max",
    "/api/power/set/50",
    "/api/volume/up",
    "/api/volume/down",
    "/api/volume/set/50",
    "/api/mute/on",
    "/api/mute/off",
    "/api/mute/toggle",
    "/api/mute-sub/toggle",
    "/api/mute-all/toggle",
    "/api/nb/on",
    "/api/nb/off",
    "/api/nr/on",
    "/api/nr/off",
    "/api/notch/on",
    "/api/notch/off",
    "/api/toggle/nb",
    "/api/toggle/nr",
    "/api/toggle/notch",
    "/api/toggle/lock",
    "/api/preamp/on",
    "/api/preamp/off",
    "/api/preamp/cycle",
    "/api/att/on",
    "/api/att/off",
    "/api/att/toggle",
    "/api/agc/fast",
    "/api/agc/cycle",
    "/api/vox/toggle",
    "/api/comp/toggle",
    "/api/mon/on",
    "/api/mon/off",
    "/api/mon/toggle",
    "/api/rit/on",
    "/api/rit/off",
    "/api/rit/toggle",
    "/api/xit/on",
    "/api/xit/off",
    "/api/xit/toggle",
    "/api/lock/on",
    "/api/lock/off",
    "/api/tune",
    "/api/tune/tgxl",
    "/api/tune/amp",
    "/api/record/start",
    "/api/record/stop",
    "/api/record/toggle",
    "/api/record/replay",
    "/api/ant/1",
    "/api/ant/2",
    "/api/ant/toggle",
    "/api/ant/rx/on",
    "/api/ant/rx/off",
    "/api/ant/rx/toggle",
    "/api/vfo-lock/on",
    "/api/vfo-lock/off",
    "/api/vfo-lock/toggle",
    "/api/remote-tx/on",
    "/api/remote-tx/off",
    "/api/remote-tx/status",
    "/api/ssb-out-level/get",
    "/api/ssb-out-level/set/50",
    "/api/tx-audio/status",
    "/api/tx-audio/devices",
    "/api/width/narrow",
    "/api/width/wide",
    "/api/cw-speed/up",
    "/api/cw-speed/down",
    "/api/cw-speed/set/20"
) | ForEach-Object { Test-Endpoint $_ 401 }

# -- ADMIN - should return 401 or 403 without admin token ---------------------
Write-Host "`nADMIN (expect 401 or 403 - admin only):" -ForegroundColor Yellow
@(
    "/api/admin/users",
    "/api/admin/sessions",
    "/api/admin/radio",
    "/api/admin/presets",
    "/api/admin/lockdown/status",
    "/api/admin/flexknob/buttons",
    "/api/admin/tx-devices"
) | ForEach-Object {
    $url = "$BASE$_"
    try {
        $resp = Invoke-WebRequest -Uri $url -Method GET -UseBasicParsing `
                    -ErrorAction SilentlyContinue
        $code = $resp.StatusCode
    } catch {
        $code = $_.Exception.Response.StatusCode.value__
        if (-not $code) { $code = 0 }
    }
    $ok = ($code -eq 401 -or $code -eq 403)
    $icon = if ($ok) { "[PASS]" } else { "[FAIL]" }
    $color = if ($ok) { "Green" } else { "Red" }
    Write-Host ("  {0} [{1,3}] {2}" -f $icon, $code, $_) -ForegroundColor $color
    if ($ok) { $script:pass++ } else { $script:fail++ }
}

# -- Summary -------------------------------------------------------------------
Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
$totalColor = if ($fail -eq 0) { "Green" } else { "Red" }
Write-Host ("  Results: {0} passed, {1} failed" -f $pass, $fail) -ForegroundColor $totalColor
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

if ($fail -gt 0) {
    Write-Host "  FAILED endpoints need investigation!" -ForegroundColor Red
    Write-Host "  - 200 on a protected route = missing auth gate" -ForegroundColor Red
    Write-Host "  - 401 on a public route    = overly restrictive" -ForegroundColor Red
}
