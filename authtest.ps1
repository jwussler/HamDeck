$BASE = "http://localhost:5002"
$p = 0; $f = 0
cls
function Chk($route, $exp) {
    try { $c = (Invoke-WebRequest -Uri ($BASE+$route) -UseBasicParsing -EA SilentlyContinue).StatusCode }
    catch { try { $c = $_.Exception.Response.StatusCode.value__ } catch { $c = 0 } }
    $ok = $c -eq $exp
    Write-Host ("  [{0}] [{1,3}] {2}" -f $(if($ok){"PASS"}else{"FAIL"}), $c, $route) -ForegroundColor $(if($ok){"Green"}else{"Red"})
    if($ok){$script:p++}else{$script:f++}
}
Write-Host "PUBLIC (expect 200):" -ForegroundColor Yellow
foreach($r in @("health","status","status/full","meters","freq","freq/get","volume/get","cw-speed/get","ant/get","ant/rx/get","auth/status","power/limit","vfo-lock/status")){Chk "/api/$r" 200}
Write-Host "PROTECTED (expect 401):" -ForegroundColor Yellow
foreach($r in @("ptt/on","ptt/off","ptt/toggle","mode/usb","vfo/swap","split/toggle","freq/set/14200000","freq/clear","band/40","preset/40ssb","power/high","mute/toggle","nb/on","nr/on","att/toggle","agc/cycle","vox/toggle","mon/off","rit/toggle","lock/on","tune","tune/tgxl","record/start","record/stop","ant/1","vfo-lock/on","vfo-lock/off","remote-tx/on","remote-tx/status","ssb-out-level/get","tx-audio/status","width/narrow","cw-speed/up")){Chk "/api/$r" 401}
Write-Host "ADMIN (expect 401 or 403):" -ForegroundColor Yellow
foreach($r in @("admin/users","admin/sessions","admin/radio","admin/presets","admin/lockdown/status","admin/flexknob/buttons")){
    try { $c = (Invoke-WebRequest -Uri ($BASE+"/api/"+$r) -UseBasicParsing -EA SilentlyContinue).StatusCode }
    catch { try { $c = $_.Exception.Response.StatusCode.value__ } catch { $c = 0 } }
    $ok = $c -eq 401 -or $c -eq 403
    Write-Host ("  [{0}] [{1,3}] /{2}" -f $(if($ok){"PASS"}else{"FAIL"}), $c, $r) -ForegroundColor $(if($ok){"Green"}else{"Red"})
    if($ok){$script:p++}else{$script:f++}
}
Write-Host ("Results: {0} passed, {1} failed" -f $p, $f) -ForegroundColor $(if($f -eq 0){"Green"}else{"Red"})
