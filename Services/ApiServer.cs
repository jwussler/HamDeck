using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using HamDeck.Helpers;
using HamDeck.Models;

namespace HamDeck.Services;

/// <summary>
/// HTTP REST API server. Port 5001: full control (localhost only).
/// Port 5002: read-only authenticated dashboard (Cloudflare-safe).
///
/// Routing: _exactRoutes (Dictionary, O(1)) + _prefixRoutes (ordered list).
/// Add new endpoints by adding an entry to BuildExactRoutes() or BuildPrefixRoutes().
/// </summary>
public class ApiServer : IDisposable
{
    private readonly RadioController _radio;
    private readonly AudioRecorder _recorder;
    private readonly TgxlTuner _tgxl;
    private readonly AmpTuner _amp;
    private readonly Config _config;
    private readonly KmtronicService? _kmtronic;
    private readonly DxClusterClient? _cluster;
    private readonly SessionStats? _stats;
    private readonly AudioStreamer? _streamer;
    private readonly AudioTransmitter? _txAudio;
    private readonly FlexKnobController? _flexknob;
    private AuthService? _auth;
    private HttpListener? _listener;
    private HttpListener? _dashboardListener;
    private CancellationTokenSource? _cts;
    private string _freqBuffer = "";
    private readonly string _wwwroot;

    private const int LocalPowerCap = 100;

    private static readonly Dictionary<string, string> MimeTypes = new()
    {
        [".html"] = "text/html; charset=utf-8",
        [".css"]  = "text/css; charset=utf-8",
        [".js"]   = "application/javascript; charset=utf-8",
        [".json"] = "application/json",
        [".ico"]  = "image/x-icon",
        [".png"]  = "image/png",
        [".svg"]  = "image/svg+xml",
    };

    private static readonly HashSet<string> ReadOnlyRoutes = new()
    {
        "/api/status", "/api/status/full", "/api/health", "/api/meters",
        "/api/session", "/api/cluster/spots", "/api/record/status",
        "/api/freq", "/api/freq/get", "/api/volume/get", "/api/cw-speed/get",
        "/api/ant/get", "/api/ant/rx/get", "/api/auth/status", "/api/power/limit",
    };

    private readonly Dictionary<string, Func<bool, object?>> _exactRoutes;
    private readonly (string Prefix, Func<string, bool, object?> Handler)[] _prefixRoutes;

    public ApiServer(RadioController radio, AudioRecorder recorder, Config config,
                     TgxlTuner tgxl, AmpTuner amp, KmtronicService? kmtronic = null,
                     DxClusterClient? cluster = null, SessionStats? stats = null,
                     AudioStreamer? streamer = null, AuthService? auth = null,
                     AudioTransmitter? txAudio = null, FlexKnobController? flexknob = null)
    {
        _radio = radio; _recorder = recorder; _config = config;
        _tgxl = tgxl; _amp = amp; _kmtronic = kmtronic;
        _cluster = cluster; _stats = stats; _streamer = streamer;
        _auth = auth; _txAudio = txAudio; _flexknob = flexknob;

        var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
        _wwwroot = Path.Combine(exeDir, "wwwroot");

        _exactRoutes  = BuildExactRoutes();
        _prefixRoutes = BuildPrefixRoutes();
    }

    // =========================================================================
    //  ROUTE TABLE
    // =========================================================================

    private Dictionary<string, Func<bool, object?>> BuildExactRoutes() => new(StringComparer.OrdinalIgnoreCase)
    {
        // Info
        ["/api/test"]   = _ => new { ok = true, message = "API is working" },
        ["/api/health"] = _ => new { status = "ok", service = "HamDeck API (C#)", version = "3.3",
                                     port = _config.APIPort, rig_connected = _radio.Connected,
                                     amp_tuning = _amp.IsActive, tgxl_tuning = _tgxl.IsActive,
                                     freq_buffer = _freqBuffer },
        ["/api/status"]      = _ => BuildApiStatus(),
        ["/api/status/full"] = _ => BuildApiStatusFull(),

        // Mode
        ["/api/mode/usb"]  = _ => { _radio.SetMode("USB");    return OK("mode", "USB"); },
        ["/api/mode/lsb"]  = _ => { _radio.SetMode("LSB");    return OK("mode", "LSB"); },
        ["/api/mode/cw"]   = _ => { _radio.SetMode("CW");     return OK("mode", "CW"); },
        ["/api/mode/am"]   = _ => { _radio.SetMode("AM");     return OK("mode", "AM"); },
        ["/api/mode/fm"]   = _ => { _radio.SetMode("FM");     return OK("mode", "FM"); },
        ["/api/mode/data"] = _ => { _radio.SetMode("DATA-U"); return OK("mode", "DATA-U"); },

        // VFO
        ["/api/vfo/a"]        = _ => { _radio.SetVFO("A");        return OK("vfo", "A"); },
        ["/api/vfo/b"]        = _ => { _radio.SetVFO("B");        return OK("vfo", "B"); },
        ["/api/vfo/swap"]     = _ => { _radio.SwapVFO();          return OK("action", "swap"); },
        ["/api/vfo-copy/a2b"] = _ => { _radio.CopyVFO("A", "B"); return OK("action", "a2b"); },
        ["/api/vfo-copy/b2a"] = _ => { _radio.CopyVFO("B", "A"); return OK("action", "b2a"); },

        // Split
        ["/api/split/on"]     = _ => { _radio.SetSplit(true);  return OK("split", 1); },
        ["/api/split/off"]    = _ => { _radio.SetSplit(false); return OK("split", 0); },
        ["/api/split/toggle"] = _ => { var c = _radio.GetSplit(); _radio.SetSplit(!c); return OK("split", !c); },
        ["/api/quick-split"]  = _ => { _radio.QuickSplit(5000); return OK("offset", 5000); },

        // PTT
        ["/api/ptt/on"]     = _ => { _radio.SetPTT(true);  return OK("ptt", 1); },
        ["/api/ptt/key"]    = _ => { _radio.SetPTT(true);  return OK("ptt", 1); },
        ["/api/ptt/off"]    = _ => { _radio.SetPTT(false); return OK("ptt", 0); },
        ["/api/ptt/unkey"]  = _ => { _radio.SetPTT(false); return OK("ptt", 0); },
        ["/api/ptt/toggle"] = _ => { var c = _radio.GetTXStatus(); _radio.SetPTT(!c); return OK("ptt", !c); },

        // Power
        ["/api/power/limit"] = local => new { status = "ok", max_watts = local ? LocalPowerCap : 200, is_local = local },
        ["/api/power/qrp"]   = _ => { _radio.SetPower(5);   return new { status = "ok", power = "qrp",  watts = 5 }; },
        ["/api/power/low"]   = _ => { _radio.SetPower(25);  return new { status = "ok", power = "low",  watts = 25 }; },
        ["/api/power/mid"]   = _ => { _radio.SetPower(50);  return new { status = "ok", power = "mid",  watts = 50 }; },
        ["/api/power/high"]  = _ => { _radio.SetPower(100); return new { status = "ok", power = "high", watts = 100 }; },
        ["/api/power/max"]   = local => { int w = local ? LocalPowerCap : 200; if (local) Logger.Info("API", "Power/max clamped to {0}W (local)", LocalPowerCap); _radio.SetPower(w); return new { status = "ok", power = local ? "high" : "max", watts = w, clamped = local }; },

        // Frequency
        ["/api/freq"]           = _ => new { freq = _radio.GetFreq() },
        ["/api/freq/clear"]     = _ => { _freqBuffer = ""; return OK("buffer", ""); },
        ["/api/freq/backspace"] = _ => { if (_freqBuffer.Length > 0) _freqBuffer = _freqBuffer[..^1]; return OK("buffer", _freqBuffer); },
        ["/api/freq/get"]       = _ => new { status = "ok", buffer = _freqBuffer, length = _freqBuffer.Length },
        ["/api/freq/send"]      = _ => SendFreqBuffer(),

        // Tuner
        ["/api/tune"]             = _ => { _radio.StartTune(); return OK("action", "tuning"); },
        ["/api/tune/tgxl"]        = _ => _tgxl.Tune(),
        ["/api/tgxl/tune"]        = _ => _tgxl.Tune(),
        ["/api/tune/tgxl/status"] = _ => new { status = "ok", tuning = _tgxl.IsActive },
        ["/api/tune/amp"]         = local => AmpTuneOrDeny(local),
        ["/api/amp/tune"]         = local => AmpTuneOrDeny(local),
        ["/api/tune/amp/status"]  = _ => new { status = "ok", tuning = _amp.IsActive },

        // Recording
        ["/api/record/status"]        = _ => _recorder.GetStatus(),
        ["/api/record/start"]         = _ => { _recorder.Start(); return OK("recording", true); },
        ["/api/record/stop"]          = _ => { var fn = _recorder.Stop(); return new { status = "ok", filename = fn }; },
        ["/api/record/replay"]        = _ => { var fn = _recorder.SaveReplay(); return new { status = "ok", filename = fn }; },
        ["/api/record/toggle"]        = _ => RecordToggle(),
        ["/api/record/toggle/stereo"] = _ => RecordToggle(),

        // Volume / mute
        ["/api/volume/get"]      = _ => { var v = _radio.GetAFGain(); return new { status = "ok", volume = v * 100 / 255, raw = v }; },
        ["/api/volume/up"]       = _ => { _radio.SetAFGain(_radio.GetAFGain() + 13); return OK(); },
        ["/api/volume/down"]     = _ => { _radio.SetAFGain(_radio.GetAFGain() - 13); return OK(); },
        ["/api/mute/on"]         = _ => { _radio.SetAFGain(0);   return OK("mute", 1); },
        ["/api/mute/off"]        = _ => { _radio.SetAFGain(128); return OK("mute", 0); },
        ["/api/mute/toggle"]     = _ => { if (_radio.GetAFGain() > 0) { _radio.SetAFGain(0); return OK("mute", 1); } _radio.SetAFGain(128); return OK("mute", 0); },
        ["/api/mute-sub/on"]     = _ => { _radio.SetSubAFGain(0);   return OK("mute_sub", 1); },
        ["/api/mute-sub/off"]    = _ => { _radio.SetSubAFGain(128); return OK("mute_sub", 0); },
        ["/api/mute-sub/toggle"] = _ => { if (_radio.GetSubAFGain() > 0) { _radio.SetSubAFGain(0); return OK("mute_sub", 1); } _radio.SetSubAFGain(128); return OK("mute_sub", 0); },
        ["/api/mute-all/on"]     = _ => { _radio.SetAFGain(0); _radio.SetSubAFGain(0);     return OK("mute_all", 1); },
        ["/api/mute-all/off"]    = _ => { _radio.SetAFGain(128); _radio.SetSubAFGain(128); return OK("mute_all", 0); },
        ["/api/mute-all/toggle"] = _ => { bool m = _radio.GetAFGain() == 0, s = _radio.GetSubAFGain() == 0; if (m && s) { _radio.SetAFGain(128); _radio.SetSubAFGain(128); return OK("mute_all", 0); } _radio.SetAFGain(0); _radio.SetSubAFGain(0); return OK("mute_all", 1); },

        // Filters / DSP
        ["/api/toggle/nb"]    = _ => { var c = _radio.GetNB();    _radio.SetNB(!c);    return OK("nb",    !c); },
        ["/api/toggle/dnr"]   = _ => { var c = _radio.GetNR();    _radio.SetNR(!c);    return OK("nr",    !c); },
        ["/api/toggle/nr"]    = _ => { var c = _radio.GetNR();    _radio.SetNR(!c);    return OK("nr",    !c); },
        ["/api/toggle/notch"] = _ => { var c = _radio.GetNotch(); _radio.SetNotch(!c); return OK("notch", !c); },
        ["/api/toggle/lock"]  = _ => { var c = _radio.GetLock();  _radio.SetLock(!c);  return OK("lock",  !c); },
        ["/api/nb/on"]        = _ => { _radio.SetNB(true);    return OK("nb",    1); },
        ["/api/nb/off"]       = _ => { _radio.SetNB(false);   return OK("nb",    0); },
        ["/api/nr/on"]        = _ => { _radio.SetNR(true);    return OK("nr",    1); },
        ["/api/nr/off"]       = _ => { _radio.SetNR(false);   return OK("nr",    0); },
        ["/api/notch/on"]     = _ => { _radio.SetNotch(true);  return OK("notch", 1); },
        ["/api/notch/off"]    = _ => { _radio.SetNotch(false); return OK("notch", 0); },
        ["/api/preamp/on"]    = _ => { _radio.SetPreamp(true);   return OK("preamp", 1); },
        ["/api/preamp/off"]   = _ => { _radio.SetPreamp(false);  return OK("preamp", 0); },
        ["/api/preamp/cycle"] = _ => { _radio.CyclePreamp();     return OK("action", "cycle"); },
        ["/api/att/on"]       = _ => { _radio.SetATT(true);  return OK("att", 1); },
        ["/api/att/off"]      = _ => { _radio.SetATT(false); return OK("att", 0); },
        ["/api/att/toggle"]   = _ => { var c = _radio.GetATT(); _radio.SetATT(!c); return OK("att", !c); },

        // AGC
        ["/api/agc/fast"]  = _ => { _radio.SetAGC("FAST"); return OK("agc", "FAST"); },
        ["/api/agc/mid"]   = _ => { _radio.SetAGC("MID");  return OK("agc", "MID"); },
        ["/api/agc/slow"]  = _ => { _radio.SetAGC("SLOW"); return OK("agc", "SLOW"); },
        ["/api/agc/off"]   = _ => { _radio.SetAGC("OFF");  return OK("agc", "OFF"); },
        ["/api/agc/auto"]  = _ => { _radio.SetAGC("AUTO"); return OK("agc", "AUTO"); },
        ["/api/agc/cycle"] = _ => { var cur = _radio.GetAGC(); var next = cur switch { "FAST"=>"MID","MID"=>"SLOW","SLOW"=>"AUTO","AUTO"=>"OFF",_=>"FAST" }; _radio.SetAGC(next); return OK("agc", next); },

        // VOX / Comp / Monitor
        ["/api/vox/on"]      = _ => { _radio.SetVOX(true);  return OK("vox",  1); },
        ["/api/vox/off"]     = _ => { _radio.SetVOX(false); return OK("vox",  0); },
        ["/api/vox/toggle"]  = _ => { var c = _radio.GetVOX();  _radio.SetVOX(!c);  return OK("vox",  !c); },
        ["/api/comp/on"]     = _ => { _radio.SetComp(true);  return OK("comp", 1); },
        ["/api/comp/off"]    = _ => { _radio.SetComp(false); return OK("comp", 0); },
        ["/api/comp/toggle"] = _ => { var c = _radio.GetComp(); _radio.SetComp(!c); return OK("comp", !c); },
        ["/api/mon/on"]      = _ => { _radio.SetMon(true);  return OK("mon", 1); },
        ["/api/mon/off"]     = _ => { _radio.SetMon(false); return OK("mon", 0); },
        ["/api/mon/toggle"]  = _ => { var c = _radio.GetMon(); _radio.SetMon(!c); return OK("mon", !c); },

        // RIT / XIT
        ["/api/rit/on"]     = _ => { _radio.SetRIT(true);  return OK("rit", 1); },
        ["/api/rit/off"]    = _ => { _radio.SetRIT(false); return OK("rit", 0); },
        ["/api/rit/toggle"] = _ => { var (on, _) = _radio.GetRIT(); _radio.SetRIT(!on); return OK("rit", !on); },
        ["/api/rit/up"]     = _ => { var (_, o) = _radio.GetRIT(); _radio.SetRITOffset(o + 100); return OK("action", "up"); },
        ["/api/rit/down"]   = _ => { var (_, o) = _radio.GetRIT(); _radio.SetRITOffset(o - 100); return OK("action", "down"); },
        ["/api/rit/clear"]  = _ => { _radio.ClearRIT(); return OK("action", "clear"); },
        ["/api/xit/on"]     = _ => { _radio.SetXIT(true);  return OK("xit", 1); },
        ["/api/xit/off"]    = _ => { _radio.SetXIT(false); return OK("xit", 0); },
        ["/api/xit/toggle"] = _ => { var c = _radio.GetXIT(); _radio.SetXIT(!c); return OK("xit", !c); },

        // CW
        ["/api/cw-speed/get"]  = _ => new { status = "ok", wpm = _radio.GetCWSpeed() },
        ["/api/cw-speed/up"]   = _ => { var w = _radio.GetCWSpeed(); _radio.SetCWSpeed(w + 2); return OK("wpm", w + 2); },
        ["/api/cw-speed/down"] = _ => { var w = _radio.GetCWSpeed(); _radio.SetCWSpeed(w - 2); return OK("wpm", w - 2); },

        // Meters
        ["/api/meters"] = _ => new { status = "ok", s_meter = _radio.GetSMeter(), swr = _radio.GetSWR(), alc = _radio.GetALC(), power = _radio.GetPowerMeter() },

        // Filter width
        ["/api/width/narrow"] = _ => { _radio.SetWidth(6);  return new { status = "ok", width = "narrow", hz = 1800 }; },
        ["/api/width/medium"] = _ => { _radio.SetWidth(10); return new { status = "ok", width = "medium", hz = 2400 }; },
        ["/api/width/wide"]   = _ => { _radio.SetWidth(14); return new { status = "ok", width = "wide",   hz = 3000 }; },

        // Lock
        ["/api/lock/on"]  = _ => { _radio.SetLock(true);  return OK("lock", 1); },
        ["/api/lock/off"] = _ => { _radio.SetLock(false); return OK("lock", 0); },

        // Antenna
        ["/api/ant/1"]         = _ => { _radio.SetAntenna(1); return OK("ant", 1); },
        ["/api/ant/2"]         = _ => { _radio.SetAntenna(2); return OK("ant", 2); },
        ["/api/ant/3"]         = _ => { _radio.SetAntenna(3); return OK("ant", 3); },
        ["/api/ant/toggle"]    = _ => { _radio.ToggleAntenna(); return OK("ant", _radio.GetAntenna()); },
        ["/api/ant/get"]       = _ => new { status = "ok", ant = _radio.GetAntenna() },
        ["/api/ant/rx/on"]     = _ => { _radio.SetRxAntenna(true);  return OK("rxant", 1); },
        ["/api/ant/rx/off"]    = _ => { _radio.SetRxAntenna(false); return OK("rxant", 0); },
        ["/api/ant/rx/toggle"] = _ => { var c = _radio.GetRxAntenna(); _radio.SetRxAntenna(!c); return OK("rxant", !c); },
        ["/api/ant/rx/get"]    = _ => new { status = "ok", rxant = _radio.GetRxAntenna() },

        // Cluster / session
        ["/api/cluster/spots"] = _ => BuildClusterSpots(),
        ["/api/session"]       = _ => BuildSessionStats(),

        // TX audio
        ["/api/tx-audio/status"]  = _ => new { status = "ok", available = _txAudio != null, active = _txAudio?.IsActive ?? false, client_connected = _txAudio?.HasClient ?? false },
        ["/api/tx-audio/devices"] = _ => new { status = "ok", devices = AudioTransmitter.ListDevices().Select(d => new { index = d.Index, name = d.Name }).ToList(), current = _config.TxAudioDevice },

        // Remote TX mode
        ["/api/remote-tx/on"]     = _ => { _radio.EnableRemoteTx();  return new { status = "ok", remote_tx = true,  message = "SSB MOD SOURCE=REAR, REAR SELECT=USB" }; },
        ["/api/remote-tx/off"]    = _ => { _radio.DisableRemoteTx(); return new { status = "ok", remote_tx = false, message = "SSB MOD SOURCE=MIC" }; },
        ["/api/remote-tx/status"] = _ => new { status = "ok", mod_source_rear = _radio.GetSSBModSourceRear(), rear_select_usb = _radio.GetRearSelectUSB(), rport_gain = _radio.GetRPortGain() },

        // SSB out level
        ["/api/ssb-out-level/get"] = _ => new { status = "ok", level = _radio.GetSSBOutLevel() },
    };

    private (string Prefix, Func<string, bool, object?> Handler)[] BuildPrefixRoutes() =>
    [
        ("/api/mode/",              (s, _)     => { var m = s.ToUpper(); _radio.SetMode(m); return OK("mode", m); }),
        ("/api/band/",              (s, _)     => RouteBand(s)),
        ("/api/preset/",            (s, _)     => RoutePreset(s)),
        ("/api/freq/set/",          (s, _)     => RouteFreqSet(s)),
        ("/api/freq/digit/",        (s, _)     => { _freqBuffer += s; return OK("buffer", _freqBuffer); }),
        ("/api/step/",              (s, _)     => RouteStep(s)),
        ("/api/memory/recall/",     (s, _)     => { if (int.TryParse(s, out var m)) { _radio.RecallMemory(m); return OK("memory", m); } return null; }),
        ("/api/power/set/",         (s, local) => RoutePowerSet(s, local)),
        ("/api/tune/amp/",          (s, local) => local ? (object?)_amp.Tune() : new { status = "error", message = "Amp tune is only available when connected locally." }),
        ("/api/volume/set/",        (s, _)     => { if (int.TryParse(s, out var vp)) { _radio.SetAFGain(vp * 255 / 100); return OK("volume", vp); } return null; }),
        ("/api/cw-speed/set/",      (s, _)     => { if (int.TryParse(s, out var w)) { _radio.SetCWSpeed(w); return OK("wpm", w); } return null; }),
        ("/api/rxant/",             (s, _)     => RouteRxAnt(s)),
        ("/api/remote-tx/gain/",    (s, _)     => { if (int.TryParse(s, out var g)) { _radio.SetRPortGain(g); return OK("rport_gain", g); } return null; }),
        ("/api/ssb-out-level/set/", (s, _)     => { if (int.TryParse(s, out var lv)) { _radio.SetSSBOutLevel(lv); return OK("ssb_out_level", lv); } return null; }),
    ];

    private object? Route(string path, bool isLocal)
    {
        if (_exactRoutes.TryGetValue(path, out var handler))
            return handler(isLocal);

        foreach (var (prefix, prefixHandler) in _prefixRoutes)
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return prefixHandler(path[prefix.Length..], isLocal);

        return null;
    }

    // =========================================================================
    //  ROUTE HELPERS
    // =========================================================================

    private object? BuildApiStatus()
    {
        if (!_radio.Connected)
            return new { connected = false, amp_tuning = _amp.IsActive, tgxl_tuning = _tgxl.IsActive, freq_buffer = _freqBuffer };
        return new { connected = true, freq = _radio.GetFreq(), mode = _radio.GetMode(), vfo = _radio.GetVFO(),
                     power = _radio.GetPower(), tx = _radio.GetTXStatus(), split = _radio.GetSplit(),
                     amp_tuning = _amp.IsActive, tgxl_tuning = _tgxl.IsActive, freq_buffer = _freqBuffer };
    }

    private object? BuildApiStatusFull()
    {
        if (!_radio.Connected) return new { connected = false };
        var rit = _radio.GetRIT();
        return new { ant = _radio.GetAntenna(), rxant = _radio.GetRxAntenna(), nb = _radio.GetNB(),
                     nr = _radio.GetNR(), notch = _radio.GetNotch(), @lock = _radio.GetLock(),
                     preamp = _radio.GetPreamp(), att = _radio.GetATT(), agc = _radio.GetAGC(),
                     vox = _radio.GetVOX(), comp = _radio.GetComp(), mon = _radio.GetMon(),
                     rit = rit.On, rit_offset = rit.Offset, xit = _radio.GetXIT(),
                     rxant_km = _kmtronic?.ActiveAntenna ?? 0 };
    }

    private object? BuildClusterSpots()
    {
        if (_cluster == null) return null;
        var spots = _cluster.Spots.Select(s => new { freq_khz = s.FreqKHz, freq_hz = s.FreqHz,
            dx_call = s.Spotted, spotter = s.Spotter, comment = s.Message, time = s.Time.ToString("o"),
            band = s.BandName, mode = s.Mode, entity = s.Entity, flag = s.Flag }).ToList();
        return new { status = "ok", spots, count = spots.Count };
    }

    private object? BuildSessionStats()
    {
        if (_stats == null) return null;
        return new { status = "ok", session_duration = _stats.SessionDuration, qsy_count = _stats.QSYCount,
                     tx_count = _stats.PTTCount, tx_time = _stats.TXTimeDisplay,
                     tx_seconds = (int)_stats.TotalTXTime.TotalSeconds, qso_count = _stats.QSOCount };
    }

    private object? RouteBand(string bandKey)
    {
        foreach (var (key, freq) in BandHelper.BandFrequencies)
            if (string.Equals(key, bandKey, StringComparison.OrdinalIgnoreCase))
            {
                var mode = BandHelper.GetModeForFrequency(freq);
                _radio.SetMode(mode); _radio.SetFreq(freq);
                return new { status = "ok", band = key, freq, mode };
            }
        return null;
    }

    private object? RoutePreset(string name)
    {
        var preset = _config.FrequencyPresets
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (preset == null)
            return new { status = "error", message = $"Preset '{name}' not found" };
        _radio.SetMode(preset.Mode); _radio.SetFreq(preset.FreqHz);
        return new { status = "ok", preset = preset.Name, freq = preset.FreqHz, mode = preset.Mode };
    }

    private object? RouteFreqSet(string suffix)
    {
        if (!long.TryParse(suffix, out var fq)) return null;
        var mode = BandHelper.GetModeForFrequency(fq);
        _radio.SetMode(mode); _radio.SetFreq(fq);
        return new { status = "ok", freq = fq, mode };
    }

    private object? RouteStep(string suffix)
    {
        var parts = suffix.Split('/');
        if (parts.Length >= 2 && long.TryParse(parts[0], out var hz))
        {
            if (parts[1] == "down") hz = -hz;
            _radio.StepFreq(hz);
            return new { status = "ok", step = Math.Abs(hz), direction = parts[1] };
        }
        return null;
    }

    private object? RoutePowerSet(string suffix, bool isLocal)
    {
        if (!int.TryParse(suffix, out var pw)) return null;
        if (isLocal && pw > LocalPowerCap) { Logger.Info("API", "Power {0}W clamped to {1}W (local)", pw, LocalPowerCap); pw = LocalPowerCap; }
        _radio.SetPower(pw);
        return new { status = "ok", power = pw, clamped = isLocal && pw == LocalPowerCap };
    }

    private object? RouteRxAnt(string suffix)
    {
        if (_kmtronic == null) return null;
        if (suffix == "get") return new { status = "ok", rxant = _kmtronic.ActiveAntenna };
        if (int.TryParse(suffix, out var rxant) && rxant >= 1 && rxant <= 4)
        { _kmtronic.SetAntenna(rxant); return OK("rxant", rxant); }
        return null;
    }

    private object? AmpTuneOrDeny(bool isLocal)
        => isLocal ? _amp.Tune() : (object)new { status = "error", message = "Amp tune is only available when connected locally." };

    private object? RecordToggle()
    {
        if (_recorder.IsRecording) { var fn = _recorder.Stop(); return new { status = "ok", action = "stopped", filename = fn }; }
        _recorder.Start(); return new { status = "ok", action = "started" };
    }

    private object SendFreqBuffer()
    {
        if (string.IsNullOrEmpty(_freqBuffer)) return new { status = "error", message = "Buffer is empty" };
        long freqHz;
        if (_freqBuffer.Length <= 3) { long.TryParse(_freqBuffer, out var mhz); freqHz = mhz * 1_000_000; }
        else { long.TryParse(_freqBuffer[..^3], out var mhz); long.TryParse(_freqBuffer[^3..], out var khz); freqHz = mhz * 1_000_000 + khz * 1_000; }
        var mode = BandHelper.GetModeForFrequency(freqHz);
        _radio.SetMode(mode); _radio.SetFreq(freqHz); _freqBuffer = "";
        return new { status = "ok", freq_hz = freqHz, mode, cleared = true };
    }

    // =========================================================================
    //  RESPONSE HELPERS
    // =========================================================================

    private static object OK() => new { status = "ok" };
    private static object OK(string key, object val) => new Dictionary<string, object> { ["status"] = "ok", [key] = val };

    private static void WriteJson(HttpListenerResponse resp, object data)
    {
        resp.ContentType = "application/json";
        var json = JsonSerializer.Serialize(data);
        var buf  = Encoding.UTF8.GetBytes(json);
        resp.ContentLength64 = buf.Length;
        resp.OutputStream.Write(buf);
        resp.Close();
    }

    // =========================================================================
    //  SERVER LIFECYCLE
    // =========================================================================

    public void Start()
    {
        _cts = new CancellationTokenSource();

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{_config.APIPort}/");
        try
        {
            _listener.Start();
            Logger.Info("API", "Control API listening on port {0}", _config.APIPort);
            Task.Run(() => ListenLoop(_listener, false, _cts.Token));
        }
        catch (HttpListenerException ex)
        {
            Logger.Warn("API", "Binding to all interfaces failed ({0}), trying localhost...", ex.Message);
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{_config.APIPort}/");
            _listener.Prefixes.Add($"http://127.0.0.1:{_config.APIPort}/");
            _listener.Start();
            Logger.Info("API", "Control API listening on localhost:{0}", _config.APIPort);
            Task.Run(() => ListenLoop(_listener, false, _cts.Token));
        }

        var dashPort = _config.APIPort + 1;
        _dashboardListener = new HttpListener();
        _dashboardListener.Prefixes.Add($"http://+:{dashPort}/");
        try
        {
            _dashboardListener.Start();
            Logger.Info("API", "Dashboard (read-only) listening on port {0}", dashPort);
            Task.Run(() => ListenLoop(_dashboardListener, true, _cts.Token));
        }
        catch (HttpListenerException ex)
        {
            Logger.Warn("API", "Dashboard binding failed ({0}), trying localhost...", ex.Message);
            _dashboardListener = new HttpListener();
            _dashboardListener.Prefixes.Add($"http://localhost:{dashPort}/");
            _dashboardListener.Prefixes.Add($"http://127.0.0.1:{dashPort}/");
            try { _dashboardListener.Start(); Logger.Info("API", "Dashboard listening on localhost:{0}", dashPort); Task.Run(() => ListenLoop(_dashboardListener, true, _cts.Token)); }
            catch (Exception ex2) { Logger.Warn("API", "Dashboard listener failed: {0}", ex2.Message); _dashboardListener = null; }
        }

        if (Directory.Exists(_wwwroot))
            Logger.Info("API", "Web dashboard available at http://localhost:{0}/", dashPort);
        else
            Logger.Warn("API", "wwwroot folder not found at {0} — web dashboard disabled", _wwwroot);
    }

    private async Task ListenLoop(HttpListener listener, bool readOnly, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            try { var ctx = await listener.GetContextAsync(); _ = Task.Run(() => HandleRequestAsync(ctx, readOnly, ct)); }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { Logger.Debug("API", "Listener error: {0}", ex.Message); }
        }
    }

    // =========================================================================
    //  REQUEST HANDLER
    // =========================================================================

    private async Task HandleRequestAsync(HttpListenerContext ctx, bool readOnly, CancellationToken ct)
    {
        var resp = ctx.Response;
        resp.Headers.Add("Access-Control-Allow-Origin", "*");
        resp.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        resp.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

        if (ctx.Request.HttpMethod == "OPTIONS") { resp.StatusCode = 200; resp.Close(); return; }

        var path = ctx.Request.Url?.AbsolutePath ?? "/";

        if (readOnly && path == "/ws" && ctx.Request.IsWebSocketRequest && _streamer != null)
        { await _streamer.HandleWebSocketClient(ctx, ct); return; }

        if (readOnly && path == "/ws/tx" && ctx.Request.IsWebSocketRequest)
        {
            if (_txAudio == null) { Logger.Warn("API", "TX audio WebSocket requested but service not available"); resp.StatusCode = 503; resp.Close(); return; }
            var token = GetSessionToken(ctx);
            if (_auth != null && (!_auth.ValidateSession(token) || !_auth.CanTransmit(token)))
            {
                Logger.Warn("API", "TX audio WebSocket rejected — not authenticated or TX not permitted");
                try { var wsCtx = await ctx.AcceptWebSocketAsync(null); await wsCtx.WebSocket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Authentication required or transmit not permitted", CancellationToken.None); }
                catch { resp.StatusCode = 401; resp.Close(); }
                return;
            }
            Logger.Info("API", "TX audio WebSocket accepted");
            await _txAudio.HandleWebSocketClient(ctx, ct); return;
        }

        if (readOnly && path == "/wsflexknob" && ctx.Request.IsWebSocketRequest && _flexknob != null)
        { await HandleFlexKnobWebSocket(ctx, ct); return; }

        if (readOnly && path == "/audio" && _streamer != null)
        {
            resp.ContentType = "text/html; charset=utf-8";
            var html = Encoding.UTF8.GetBytes(_streamer.GetPlayerHtml());
            resp.ContentLength64 = html.Length; resp.OutputStream.Write(html); resp.Close(); return;
        }

        if (path.StartsWith("/api/"))
        {
            var trimmed = path.TrimEnd('/');

            if (readOnly && trimmed == "/api/auth/login" && ctx.Request.HttpMethod == "POST") { await HandleLogin(ctx); return; }
            if (readOnly && trimmed == "/api/auth/logout") { HandleLogout(ctx); return; }
            if (readOnly && trimmed == "/api/auth/status")
            {
                var token = GetSessionToken(ctx);
                var valid = _auth?.ValidateSession(token) ?? false;
                WriteJson(resp, new { status = "ok", authenticated = valid, is_admin = valid && (_auth?.IsAdmin(token) ?? false),
                    can_transmit = valid && (_auth?.CanTransmit(token) ?? false), username = valid ? _auth?.GetUsername(token) : null,
                    token = valid ? token : null });
                return;
            }
            if (readOnly && trimmed == "/api/auth/setup" && ctx.Request.HttpMethod == "POST") { await HandlePasswordSetup(ctx); return; }

            if (readOnly && trimmed.StartsWith("/api/admin/"))
            {
                var token = GetSessionToken(ctx);
                if (_auth == null || !_auth.IsAdmin(token)) { resp.StatusCode = 403; WriteJson(resp, new { status = "error", message = "Admin access required" }); return; }
                var adminResult = await HandleAdminRoute(trimmed, ctx);
                if (adminResult != null) { WriteJson(resp, adminResult); return; }
            }

            if (readOnly && (trimmed == "/api/ptt/on" || trimmed == "/api/ptt/off" || trimmed == "/api/ptt/key" || trimmed == "/api/ptt/unkey" || trimmed == "/api/ptt/toggle"))
            {
                var token = GetSessionToken(ctx);
                if (_auth != null && !_auth.CanTransmit(token)) { resp.StatusCode = 403; WriteJson(resp, new { status = "error", message = "Transmit not permitted for this account" }); return; }
            }

            if (readOnly && !ReadOnlyRoutes.Contains(trimmed))
            {
                var token = GetSessionToken(ctx);
                if (_auth == null || !_auth.ValidateSession(token)) { resp.StatusCode = 401; WriteJson(resp, new { status = "error", message = "Authentication required" }); return; }
            }

            bool isLocal = IsLocalRequest(ctx);
            object? result = Route(trimmed, isLocal);
            WriteJson(resp, result ?? new { status = "error", message = "Unknown route", path });
            return;
        }

        if (TryServeFile(path, resp)) return;

        if (readOnly) WriteJson(resp, new { service = "HamDeck Dashboard (read-only)", port = _config.APIPort + 1 });
        else          WriteJson(resp, new { service = "HamDeck API (C#)", port = _config.APIPort, dashboard = Directory.Exists(_wwwroot) });
    }

    private bool TryServeFile(string path, HttpListenerResponse resp)
    {
        if (!Directory.Exists(_wwwroot)) return false;
        if (path == "/" || path == "") path = "/index.html";
        var relativePath = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_wwwroot, relativePath));
        if (!fullPath.StartsWith(Path.GetFullPath(_wwwroot))) return false;
        if (!File.Exists(fullPath)) return false;
        try
        {
            var ext = Path.GetExtension(fullPath).ToLower();
            resp.ContentType = MimeTypes.GetValueOrDefault(ext, "application/octet-stream");
            resp.Headers.Add("Cache-Control", ext != ".html" ? "public, max-age=300" : "no-cache");
            var bytes = File.ReadAllBytes(fullPath);
            resp.ContentLength64 = bytes.Length; resp.OutputStream.Write(bytes); resp.Close();
            return true;
        }
        catch (Exception ex) { Logger.Debug("API", "Static file error: {0}", ex.Message); return false; }
    }

    private static bool IsLocalRequest(HttpListenerContext ctx)
    {
        if (!string.IsNullOrEmpty(ctx.Request.Headers["CF-Connecting-IP"])) { Logger.Debug("API", "IsLocal=false (CF-Connecting-IP present)"); return false; }
        var remoteAddr = ctx.Request.RemoteEndPoint?.Address;
        if (remoteAddr == null) return true;
        if (remoteAddr.IsIPv4MappedToIPv6) remoteAddr = remoteAddr.MapToIPv4();
        if (System.Net.IPAddress.IsLoopback(remoteAddr)) return true;
        var bytes = remoteAddr.GetAddressBytes();
        if (bytes.Length == 4)
        {
            if (bytes[0] == 10) return true;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            if (bytes[0] == 192 && bytes[1] == 168) return true;
        }
        Logger.Debug("API", "IsLocal=false (remote addr: {0})", remoteAddr);
        return false;
    }

    // =========================================================================
    //  AUTH HELPERS
    // =========================================================================

    private string? GetSessionToken(HttpListenerContext ctx)
    {
        var cookie = ctx.Request.Cookies["hamdeck_session"];
        if (cookie != null) return cookie.Value;
        var authHeader = ctx.Request.Headers["Authorization"];
        if (authHeader != null && authHeader.StartsWith("Bearer ")) return authHeader[7..];
        var query = ctx.Request.QueryString["token"];
        if (!string.IsNullOrEmpty(query)) return query;
        return null;
    }

    private async Task HandleLogin(HttpListenerContext ctx)
    {
        var resp = ctx.Response;
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var login = JsonSerializer.Deserialize<LoginRequest>(await reader.ReadToEndAsync());
            if (login == null || _auth == null) { resp.StatusCode = 400; WriteJson(resp, new { status = "error", message = "Invalid request" }); return; }

            var token = _auth.Login(login.Username ?? "", login.Password ?? "");
            if (token == null) { await Task.Delay(500); resp.StatusCode = 401; WriteJson(resp, new { status = "error", message = "Invalid credentials" }); return; }

            if (_config.AdminOnlyLogin && !_auth.IsAdmin(token))
            { _auth.Logout(token); await Task.Delay(300); resp.StatusCode = 403; WriteJson(resp, new { status = "error", message = "Login is currently restricted to administrators" }); return; }

            var maxAge = (_config.WebSessionTimeout > 0 ? _config.WebSessionTimeout : 480) * 60;
            resp.Headers.Add("Set-Cookie", $"hamdeck_session={token}; Path=/; HttpOnly; SameSite=Lax; Max-Age={maxAge}");
            WriteJson(resp, new { status = "ok", message = "Login successful" });
        }
        catch (Exception ex) { Logger.Warn("AUTH", "Login error: {0}", ex.Message); resp.StatusCode = 500; WriteJson(resp, new { status = "error", message = "Internal error" }); }
    }

    private void HandleLogout(HttpListenerContext ctx)
    {
        _auth?.Logout(GetSessionToken(ctx));
        ctx.Response.Headers.Add("Set-Cookie", "hamdeck_session=; Path=/; HttpOnly; Max-Age=0");
        WriteJson(ctx.Response, new { status = "ok", message = "Logged out" });
    }

    private async Task HandlePasswordSetup(HttpListenerContext ctx)
    {
        var resp = ctx.Response;
        if (_auth != null && _auth.IsConfigured) { resp.StatusCode = 403; WriteJson(resp, new { status = "error", message = "Password already configured. Use admin panel to add users." }); return; }
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var setup = JsonSerializer.Deserialize<SetupRequest>(await reader.ReadToEndAsync());
            if (setup == null || string.IsNullOrWhiteSpace(setup.Password) || setup.Password.Length < 6) { resp.StatusCode = 400; WriteJson(resp, new { status = "error", message = "Password must be at least 6 characters" }); return; }

            var username = setup.Username?.Trim().ToLower() ?? "wa0o";
            var hash = AuthService.HashPassword(setup.Password);
            if (_auth == null) _auth = new AuthService(_config.WebSessionTimeout);
            _auth.AddUser(username, hash, isAdmin: true, canTransmit: true);
            _config.WebUsers.Add(new Config.WebUser { Username = username, PasswordHash = hash, IsAdmin = true, CanTransmit = true });
            _config.WebPasswordHash = hash; _config.WebUsername = username; _config.Save();
            Logger.Info("AUTH", "Initial admin user '{0}' created", username);
            WriteJson(resp, new { status = "ok", message = "Admin account created. Please log in." });
        }
        catch (Exception ex) { resp.StatusCode = 500; WriteJson(resp, new { status = "error", message = ex.Message }); }
    }

    // =========================================================================
    //  ADMIN HANDLERS
    // =========================================================================

    private async Task<object?> HandleAdminRoute(string path, HttpListenerContext ctx)
    {
        if (path == "/api/admin/users")    return new { status = "ok", users    = _auth!.GetUsers() };
        if (path == "/api/admin/sessions") return new { status = "ok", sessions = _auth!.GetActiveSessions() };

        if (path == "/api/admin/user/add" && ctx.Request.HttpMethod == "POST")
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var req = JsonSerializer.Deserialize<AdminUserRequest>(await reader.ReadToEndAsync());
            if (req == null || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 6)
                return new { status = "error", message = "Username and password (6+ chars) required" };
            var hash = AuthService.HashPassword(req.Password);
            _auth!.AddUser(req.Username, hash, req.IsAdmin, req.CanTransmit);
            _config.WebUsers.Add(new Config.WebUser { Username = req.Username.Trim().ToLower(), PasswordHash = hash, IsAdmin = req.IsAdmin, CanTransmit = req.CanTransmit });
            _config.Save();
            return new { status = "ok", message = $"User '{req.Username}' added" };
        }

        if (path.StartsWith("/api/admin/user/remove/"))
        {
            var username = path["/api/admin/user/remove/".Length..];
            if (!_auth!.RemoveUser(username)) return new { status = "error", message = "User not found" };
            _config.WebUsers.RemoveAll(u => u.Username.Equals(username.Trim(), StringComparison.OrdinalIgnoreCase));
            _config.Save(); return new { status = "ok", message = $"User '{username}' removed" };
        }

        if (path == "/api/admin/user/password" && ctx.Request.HttpMethod == "POST")
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var req = JsonSerializer.Deserialize<AdminPasswordRequest>(await reader.ReadToEndAsync());
            if (req == null || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 6)
                return new { status = "error", message = "Username and new password (6+ chars) required" };
            var hash = AuthService.HashPassword(req.NewPassword);
            if (!_auth!.ChangePassword(req.Username, hash)) return new { status = "error", message = "User not found" };
            var user = _config.WebUsers.Find(u => u.Username.Equals(req.Username.Trim(), StringComparison.OrdinalIgnoreCase));
            if (user != null) user.PasswordHash = hash;
            _config.Save(); return new { status = "ok", message = $"Password changed for '{req.Username}'" };
        }

        if (path.StartsWith("/api/admin/kick/"))
        { var u = path["/api/admin/kick/".Length..]; var n = _auth!.KillUserSessions(u); return new { status = "ok", kicked = n, message = $"Killed {n} sessions for '{u}'" }; }

        if (path == "/api/admin/lockdown/on")  { _config.AdminOnlyLogin = true;  _config.Save(); Logger.Info("AUTH", "Admin-only lockdown ENABLED");  return new { status = "ok", admin_only_login = true,  message = "Login restricted to admins" }; }
        if (path == "/api/admin/lockdown/off") { _config.AdminOnlyLogin = false; _config.Save(); Logger.Info("AUTH", "Admin-only lockdown DISABLED"); return new { status = "ok", admin_only_login = false, message = "All users may log in" }; }
        if (path == "/api/admin/lockdown/status") return new { status = "ok", admin_only_login = _config.AdminOnlyLogin };

        if (path.StartsWith("/api/admin/user/tx/"))
        {
            var rem = path["/api/admin/user/tx/".Length..];
            var slash = rem.IndexOf('/');
            if (slash > 0)
            {
                bool allow = rem[..slash] == "enable";
                var username = rem[(slash + 1)..];
                if (!_auth!.SetUserCanTransmit(username, allow)) return new { status = "error", message = $"User '{username}' not found" };
                var cfgUser = _config.WebUsers.Find(u => u.Username.Equals(username.Trim(), StringComparison.OrdinalIgnoreCase));
                if (cfgUser != null) cfgUser.CanTransmit = allow;
                _config.Save(); return new { status = "ok", username, can_transmit = allow };
            }
        }

        if (path == "/api/admin/mic/release") { _txAudio?.Stop(); return new { status = "ok", message = "TX audio client disconnected" }; }

        if (path == "/api/admin/radio") return new { status = "ok", connected = _radio.Connected, port = _radio.PortName,
            freq = _radio.LastFrequency, mode = _radio.LastMode, tx = _radio.LastTXState,
            tx_audio_active = _txAudio?.IsActive ?? false, tx_audio_client = _txAudio?.HasClient ?? false,
            proxy_active = _radio.ProxyIsActive, sessions = _auth!.ActiveSessionCount, admin_only_login = _config.AdminOnlyLogin };

        if (path == "/api/admin/tx-devices") { var d = AudioTransmitter.ListDevices(); return new { status = "ok", devices = d.Select(x => new { index = x.Index, name = x.Name }).ToList(), current = _config.TxAudioDevice }; }

        if (path.StartsWith("/api/admin/rport-gain/") && int.TryParse(path["/api/admin/rport-gain/".Length..], out var gain))
        { _radio.SetRPortGain(gain); return new { status = "ok", rport_gain = gain }; }

        // Preset management
        if (path == "/api/admin/presets") return new { status = "ok", presets = _config.FrequencyPresets };

        if (path == "/api/admin/presets/add" && ctx.Request.HttpMethod == "POST")
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var preset = JsonSerializer.Deserialize<Config.FrequencyPreset>(await reader.ReadToEndAsync());
            if (preset == null || string.IsNullOrWhiteSpace(preset.Name) || preset.FreqHz <= 0) return new { status = "error", message = "name and freq_hz required" };
            _config.FrequencyPresets.RemoveAll(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
            _config.FrequencyPresets.Add(preset); _config.Save();
            return new { status = "ok", message = $"Preset '{preset.Name}' saved" };
        }

        if (path.StartsWith("/api/admin/presets/remove/"))
        {
            var name = path["/api/admin/presets/remove/".Length..];
            var removed = _config.FrequencyPresets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) { _config.Save(); return new { status = "ok", message = $"Preset '{name}' removed" }; }
            return new { status = "error", message = $"Preset '{name}' not found" };
        }

        return null;
    }

    private class LoginRequest       { [JsonPropertyName("username")] public string? Username { get; set; } [JsonPropertyName("password")]     public string? Password    { get; set; } }
    private class SetupRequest       { [JsonPropertyName("username")] public string? Username { get; set; } [JsonPropertyName("password")]     public string? Password    { get; set; } }
    private class AdminUserRequest   { [JsonPropertyName("username")] public string? Username { get; set; } [JsonPropertyName("password")]     public string? Password    { get; set; } [JsonPropertyName("is_admin")] public bool IsAdmin { get; set; } [JsonPropertyName("can_transmit")] public bool CanTransmit { get; set; } = true; }
    private class AdminPasswordRequest { [JsonPropertyName("username")] public string? Username { get; set; } [JsonPropertyName("new_password")] public string? NewPassword { get; set; } }

    // =========================================================================
    //  FLEXKNOB WebSocket
    // =========================================================================

    private async Task HandleFlexKnobWebSocket(HttpListenerContext ctx, CancellationToken ct)
    {
        HttpListenerWebSocketContext? wsCtx = null;
        try { wsCtx = await ctx.AcceptWebSocketAsync(null); }
        catch (Exception ex) { Logger.Warn("FLEXKNOB-WS", "WebSocket upgrade failed: {0}", ex.Message); ctx.Response.StatusCode = 500; ctx.Response.Close(); return; }

        var ws = wsCtx.WebSocket;
        var sendLock = new SemaphoreSlim(1, 1);
        Logger.Info("FLEXKNOB-WS", "Browser client connected from {0}", ctx.Request.RemoteEndPoint);

        async Task SafeSend(object data)
        {
            if (ws.State != WebSocketState.Open) return;
            var buf = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data));
            await sendLock.WaitAsync(ct);
            try { if (ws.State == WebSocketState.Open) await ws.SendAsync(buf, WebSocketMessageType.Text, true, ct); }
            catch { } finally { sendLock.Release(); }
        }

        Action<string> onMode   = mode   => _ = SafeSend(new { type = "mode",   mode, step = _flexknob!.StepDisplay });
        Action<string> onAction = action => _ = SafeSend(new { type = "action", action });
        _flexknob!.OnModeChanged += onMode;
        _flexknob!.OnAction      += onAction;
        await SafeSend(new { type = "state", mode = _flexknob.ModeName, step = _flexknob.StepDisplay, hw_connected = _flexknob.IsConnected });

        try
        {
            var buf = new byte[4096];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                try { result = await ws.ReceiveAsync(buf, ct); } catch { break; }
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType != WebSocketMessageType.Text) continue;
                var text = Encoding.UTF8.GetString(buf, 0, result.Count);
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    var msgType = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (msgType == "flexknob")
                    {
                        var cmd = doc.RootElement.TryGetProperty("cmd", out var c) ? c.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(cmd)) { Logger.Debug("FLEXKNOB-WS", "Inject: {0}", cmd); _flexknob.InjectCommand(cmd!); }
                    }
                    else if (msgType == "ping") { await SafeSend(new { type = "pong" }); }
                }
                catch (JsonException ex) { Logger.Warn("FLEXKNOB-WS", "Bad JSON: {0}", ex.Message); }
            }
        }
        finally
        {
            _flexknob.OnModeChanged -= onMode;
            _flexknob.OnAction      -= onAction;
            try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
            Logger.Info("FLEXKNOB-WS", "Browser client disconnected");
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        try { _dashboardListener?.Stop(); } catch { }
        _listener = null; _dashboardListener = null;
    }
}
