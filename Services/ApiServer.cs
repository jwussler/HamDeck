using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HamDeck.Helpers;
using HamDeck.Models;

namespace HamDeck.Services;

/// <summary>
/// HTTP REST API server compatible with Stream Deck and the original Python daemon.
/// Runs on the configured port (default 5001).
/// </summary>
public class ApiServer : IDisposable
{
    private readonly RadioController _radio;
    private readonly AudioRecorder _recorder;
    private readonly TgxlTuner _tgxl;
    private readonly AmpTuner _amp;
    private readonly Config _config;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private string _freqBuffer = "";

    public ApiServer(RadioController radio, AudioRecorder recorder, Config config,
                     TgxlTuner tgxl, AmpTuner amp)
    {
        _radio = radio;
        _recorder = recorder;
        _config = config;
        _tgxl = tgxl;
        _amp = amp;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{_config.APIPort}/");

        try
        {
            _listener.Start();
            Logger.Info("API", "Listening on port {0}", _config.APIPort);
            Task.Run(() => ListenLoop(_cts.Token));
        }
        catch (HttpListenerException ex)
        {
            // Fallback to localhost only if + prefix fails (no admin rights)
            Logger.Warn("API", "Binding to all interfaces failed ({0}), trying localhost...", ex.Message);
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{_config.APIPort}/");
            _listener.Prefixes.Add($"http://127.0.0.1:{_config.APIPort}/");
            _listener.Start();
            Logger.Info("API", "Listening on localhost:{0}", _config.APIPort);
            Task.Run(() => ListenLoop(_cts.Token));
        }
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener?.IsListening == true)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(ctx));
            }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { Logger.Debug("API", "Listener error: {0}", ex.Message); }
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        var resp = ctx.Response;
        resp.Headers.Add("Access-Control-Allow-Origin", "*");
        resp.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        resp.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

        if (ctx.Request.HttpMethod == "OPTIONS") { resp.StatusCode = 200; resp.Close(); return; }

        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        object? result = Route(path);
        WriteJson(resp, result ?? new { status = "error", message = "Unknown route" });
    }

    private object? Route(string path)
    {
        // Strip trailing slash for matching
        path = path.TrimEnd('/');

        // ===== INDEX / HEALTH =====
        if (path == "" || path == "/") return new { service = "HamDeck API (C#)", port = _config.APIPort };
        if (path == "/api/test") return new { ok = true, message = "API is working" };
        if (path == "/api/health") return new { status = "ok", service = "HamDeck API (C#)", version = "2.0", port = _config.APIPort, rig_connected = _radio.Connected, amp_tuning = _amp.IsActive, tgxl_tuning = _tgxl.IsActive, freq_buffer = _freqBuffer };

        // ===== STATUS =====
        if (path == "/api/status")
        {
            if (!_radio.Connected)
                return new { connected = false, amp_tuning = _amp.IsActive, tgxl_tuning = _tgxl.IsActive, freq_buffer = _freqBuffer };
            return new { connected = true, freq = _radio.GetFreq(), mode = _radio.GetMode(), vfo = _radio.GetVFO(), power = _radio.GetPower(), tx = _radio.GetTXStatus(), split = _radio.GetSplit(), antenna = _radio.GetAntenna(), amp_tuning = _amp.IsActive, tgxl_tuning = _tgxl.IsActive, freq_buffer = _freqBuffer };
        }

        // ===== MODE =====
        if (path == "/api/mode/usb") { _radio.SetMode("USB"); return OK("mode", "USB"); }
        if (path == "/api/mode/lsb") { _radio.SetMode("LSB"); return OK("mode", "LSB"); }
        if (path == "/api/mode/cw") { _radio.SetMode("CW"); return OK("mode", "CW"); }
        if (path == "/api/mode/am") { _radio.SetMode("AM"); return OK("mode", "AM"); }
        if (path == "/api/mode/fm") { _radio.SetMode("FM"); return OK("mode", "FM"); }
        if (path == "/api/mode/data") { _radio.SetMode("DATA-U"); return OK("mode", "DATA-U"); }
        if (path.StartsWith("/api/mode/")) { var m = path["/api/mode/".Length..].ToUpper(); _radio.SetMode(m); return OK("mode", m); }

        // ===== BAND =====
        foreach (var (key, freq) in BandHelper.BandFrequencies)
        {
            if (path == $"/api/band/{key}")
            {
                var mode = BandHelper.GetModeForFrequency(freq);
                _radio.SetMode(mode); _radio.SetFreq(freq);
                return new { status = "ok", band = key, freq, mode };
            }
        }

        // ===== VFO =====
        if (path == "/api/vfo/a") { _radio.SetVFO("A"); return OK("vfo", "A"); }
        if (path == "/api/vfo/b") { _radio.SetVFO("B"); return OK("vfo", "B"); }
        if (path == "/api/vfo/swap") { _radio.SwapVFO(); return OK("action", "swap"); }
        if (path == "/api/vfo-copy/a2b") { _radio.CopyVFO("A", "B"); return OK("action", "a2b"); }
        if (path == "/api/vfo-copy/b2a") { _radio.CopyVFO("B", "A"); return OK("action", "b2a"); }

        // ===== SPLIT =====
        if (path == "/api/split/on") { _radio.SetSplit(true); return OK("split", 1); }
        if (path == "/api/split/off") { _radio.SetSplit(false); return OK("split", 0); }
        if (path == "/api/split/toggle") { var c = _radio.GetSplit(); _radio.SetSplit(!c); return OK("split", !c); }
        if (path == "/api/quick-split") { _radio.QuickSplit(5000); return OK("offset", 5000); }

        // ===== PTT =====
        if (path is "/api/ptt/on" or "/api/ptt/key") { _radio.SetPTT(true); return OK("ptt", 1); }
        if (path is "/api/ptt/off" or "/api/ptt/unkey") { _radio.SetPTT(false); return OK("ptt", 0); }

        // ===== POWER =====
        if (path == "/api/power/qrp") { _radio.SetPower(5); return new { status = "ok", power = "qrp", watts = 5 }; }
        if (path == "/api/power/low") { _radio.SetPower(25); return new { status = "ok", power = "low", watts = 25 }; }
        if (path == "/api/power/mid") { _radio.SetPower(50); return new { status = "ok", power = "mid", watts = 50 }; }
        if (path == "/api/power/high") { _radio.SetPower(100); return new { status = "ok", power = "high", watts = 100 }; }
        if (path == "/api/power/max") { _radio.SetPower(200); return new { status = "ok", power = "max", watts = 200 }; }
        if (path.StartsWith("/api/power/set/") && int.TryParse(path["/api/power/set/".Length..], out var pw))
        { _radio.SetPower(pw); return OK("power", pw); }

        // ===== FREQUENCY =====
        if (path == "/api/freq") return new { freq = _radio.GetFreq() };
        if (path.StartsWith("/api/freq/set/") && long.TryParse(path["/api/freq/set/".Length..], out var fq))
        {
            var mode = BandHelper.GetModeForFrequency(fq); _radio.SetMode(mode); _radio.SetFreq(fq);
            return new { status = "ok", freq = fq, mode };
        }

        // ===== FREQ KEYPAD BUFFER =====
        if (path.StartsWith("/api/freq/digit/") && path.Length > "/api/freq/digit/".Length)
        { var d = path["/api/freq/digit/".Length..]; _freqBuffer += d; return OK("buffer", _freqBuffer); }
        if (path == "/api/freq/clear") { _freqBuffer = ""; return OK("buffer", ""); }
        if (path == "/api/freq/backspace") { if (_freqBuffer.Length > 0) _freqBuffer = _freqBuffer[..^1]; return OK("buffer", _freqBuffer); }
        if (path == "/api/freq/get") return new { status = "ok", buffer = _freqBuffer, length = _freqBuffer.Length };
        if (path == "/api/freq/send") return SendFreqBuffer();

        // ===== STEP =====
        if (path.StartsWith("/api/step/"))
        {
            var parts = path.Split('/');
            if (parts.Length >= 5 && long.TryParse(parts[3], out var hz))
            {
                if (parts[4] == "down") hz = -hz;
                _radio.StepFreq(hz);
                return new { status = "ok", step = Math.Abs(hz), direction = parts[4] };
            }
        }

        // ===== TUNERS =====
        if (path == "/api/tune") { _radio.StartTune(); return OK("action", "tuning"); }
        if (path is "/api/tune/tgxl" or "/api/tgxl/tune") return _tgxl.Tune();
        if (path == "/api/tune/tgxl/status") return new { status = "ok", tuning = _tgxl.IsActive };
        if (path is "/api/tune/amp" or "/api/amp/tune") return _amp.Tune(30, 0.15);
        if (path == "/api/tune/amp/status") return new { status = "ok", tuning = _amp.IsActive };
        if (path.StartsWith("/api/tune/amp/"))
        {
            var parts = path.Split('/');
            double power = 0.15; int dur = 30;
            if (parts.Length >= 5 && parts[4] != "status" && double.TryParse(parts[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p)) power = p;
            if (parts.Length >= 6 && int.TryParse(parts[5], out var d)) dur = d;
            return _amp.Tune(dur, power);
        }

        // ===== RECORDING =====
        if (path == "/api/record/status") return _recorder.GetStatus();
        if (path == "/api/record/start") { _recorder.Start(); return OK("recording", true); }
        if (path == "/api/record/stop") { var fn = _recorder.Stop(); return new { status = "ok", filename = fn }; }
        if (path is "/api/record/toggle" or "/api/record/toggle/stereo")
        {
            if (_recorder.IsRecording) { var fn = _recorder.Stop(); return new { status = "ok", action = "stopped", filename = fn }; }
            _recorder.Start(); return new { status = "ok", action = "started" };
        }
        if (path == "/api/record/replay") { var fn = _recorder.SaveReplay(); return new { status = "ok", filename = fn }; }

        // ===== VOLUME =====
        if (path == "/api/volume/get") { var v = _radio.GetAFGain(); return new { status = "ok", volume = v * 100 / 255, raw = v }; }
        if (path == "/api/volume/up") { _radio.SetAFGain(_radio.GetAFGain() + 13); return OK(); }
        if (path == "/api/volume/down") { _radio.SetAFGain(_radio.GetAFGain() - 13); return OK(); }
        if (path.StartsWith("/api/volume/set/") && int.TryParse(path["/api/volume/set/".Length..], out var vp))
        { _radio.SetAFGain(vp * 255 / 100); return OK("volume", vp); }
        if (path == "/api/mute/on") { _radio.SetAFGain(0); return OK("mute", 1); }
        if (path == "/api/mute/off") { _radio.SetAFGain(128); return OK("mute", 0); }
        if (path == "/api/mute/toggle") { if (_radio.GetAFGain() > 0) { _radio.SetAFGain(0); return OK("mute", 1); } _radio.SetAFGain(128); return OK("mute", 0); }

        // ===== FILTERS =====
        if (path == "/api/toggle/nb") { var c = _radio.GetNB(); _radio.SetNB(!c); return OK("nb", !c); }
        if (path == "/api/toggle/dnr" || path == "/api/toggle/nr") { var c = _radio.GetNR(); _radio.SetNR(!c); return OK("nr", !c); }
        if (path == "/api/toggle/notch") { var c = _radio.GetNotch(); _radio.SetNotch(!c); return OK("notch", !c); }
        if (path == "/api/toggle/lock") { var c = _radio.GetLock(); _radio.SetLock(!c); return OK("lock", !c); }
        if (path == "/api/nb/on") { _radio.SetNB(true); return OK("nb", 1); }
        if (path == "/api/nb/off") { _radio.SetNB(false); return OK("nb", 0); }
        if (path == "/api/nr/on") { _radio.SetNR(true); return OK("nr", 1); }
        if (path == "/api/nr/off") { _radio.SetNR(false); return OK("nr", 0); }
        if (path == "/api/notch/on") { _radio.SetNotch(true); return OK("notch", 1); }
        if (path == "/api/notch/off") { _radio.SetNotch(false); return OK("notch", 0); }

        // ===== PREAMP / ATT =====
        if (path == "/api/preamp/on") { _radio.SetPreamp(true); return OK("preamp", 1); }
        if (path == "/api/preamp/off") { _radio.SetPreamp(false); return OK("preamp", 0); }
        if (path == "/api/preamp/cycle") { _radio.CyclePreamp(); return OK("action", "cycle"); }
        if (path == "/api/att/on") { _radio.SetATT(true); return OK("att", 1); }
        if (path == "/api/att/off") { _radio.SetATT(false); return OK("att", 0); }
        if (path == "/api/att/toggle") { var c = _radio.GetATT(); _radio.SetATT(!c); return OK("att", !c); }

        // ===== AGC =====
        if (path == "/api/agc/fast") { _radio.SetAGC("FAST"); return OK("agc", "FAST"); }
        if (path == "/api/agc/mid") { _radio.SetAGC("MID"); return OK("agc", "MID"); }
        if (path == "/api/agc/slow") { _radio.SetAGC("SLOW"); return OK("agc", "SLOW"); }
        if (path == "/api/agc/off") { _radio.SetAGC("OFF"); return OK("agc", "OFF"); }
        if (path == "/api/agc/auto") { _radio.SetAGC("AUTO"); return OK("agc", "AUTO"); }

        // ===== VOX / COMP =====
        if (path == "/api/vox/on") { _radio.SetVOX(true); return OK("vox", 1); }
        if (path == "/api/vox/off") { _radio.SetVOX(false); return OK("vox", 0); }
        if (path == "/api/vox/toggle") { var c = _radio.GetVOX(); _radio.SetVOX(!c); return OK("vox", !c); }
        if (path == "/api/comp/on") { _radio.SetComp(true); return OK("comp", 1); }
        if (path == "/api/comp/off") { _radio.SetComp(false); return OK("comp", 0); }
        if (path == "/api/comp/toggle") { var c = _radio.GetComp(); _radio.SetComp(!c); return OK("comp", !c); }

        // ===== RIT / XIT =====
        if (path == "/api/rit/on") { _radio.SetRIT(true); return OK("rit", 1); }
        if (path == "/api/rit/off") { _radio.SetRIT(false); return OK("rit", 0); }
        if (path == "/api/rit/up") { var (_, o) = _radio.GetRIT(); _radio.SetRITOffset(o + 100); return OK("action", "up"); }
        if (path == "/api/rit/down") { var (_, o) = _radio.GetRIT(); _radio.SetRITOffset(o - 100); return OK("action", "down"); }
        if (path == "/api/rit/clear") { _radio.ClearRIT(); return OK("action", "clear"); }
        if (path == "/api/xit/on") { _radio.SetXIT(true); return OK("xit", 1); }
        if (path == "/api/xit/off") { _radio.SetXIT(false); return OK("xit", 0); }

        // ===== ANTENNA =====
        if (path == "/api/antenna") return new { status = "ok", antenna = _radio.GetAntenna() };
        if (path == "/api/antenna/1") { _radio.SetAntenna(1); return OK("antenna", 1); }
        if (path == "/api/antenna/2") { _radio.SetAntenna(2); return OK("antenna", 2); }
        if (path == "/api/antenna/3") { _radio.SetAntenna(3); return OK("antenna", 3); }
        if (path == "/api/antenna/toggle") { _radio.ToggleAntenna(); return new { status = "ok", antenna = _radio.GetAntenna() }; }

        // ===== CW =====
        if (path == "/api/cw-speed/get") return new { status = "ok", wpm = _radio.GetCWSpeed() };
        if (path == "/api/cw-speed/up") { var w = _radio.GetCWSpeed(); _radio.SetCWSpeed(w + 2); return OK("wpm", w + 2); }
        if (path == "/api/cw-speed/down") { var w = _radio.GetCWSpeed(); _radio.SetCWSpeed(w - 2); return OK("wpm", w - 2); }
        if (path.StartsWith("/api/cw-speed/set/") && int.TryParse(path["/api/cw-speed/set/".Length..], out var wpm))
        { _radio.SetCWSpeed(wpm); return OK("wpm", wpm); }

        // ===== METERS =====
        if (path == "/api/meters") return new { status = "ok", s_meter = _radio.GetSMeter(), swr = _radio.GetSWR(), alc = _radio.GetALC(), power = _radio.GetPowerMeter() };

        // ===== MEMORY =====
        if (path.StartsWith("/api/memory/recall/") && int.TryParse(path["/api/memory/recall/".Length..], out var mem))
        { _radio.RecallMemory(mem); return OK("memory", mem); }

        // ===== WIDTH =====
        if (path == "/api/width/narrow") { _radio.SetWidth(6); return new { status = "ok", width = "narrow", hz = 1800 }; }
        if (path == "/api/width/medium") { _radio.SetWidth(10); return new { status = "ok", width = "medium", hz = 2400 }; }
        if (path == "/api/width/wide") { _radio.SetWidth(14); return new { status = "ok", width = "wide", hz = 3000 }; }

        // ===== PRESETS =====
        if (path == "/api/preset/40cw") { _radio.SetFreq(7030000); _radio.SetMode("CW"); return OK("preset", "40cw"); }
        if (path == "/api/preset/40ssb") { _radio.SetFreq(7200000); _radio.SetMode("LSB"); return OK("preset", "40ssb"); }
        if (path == "/api/preset/20cw") { _radio.SetFreq(14030000); _radio.SetMode("CW"); return OK("preset", "20cw"); }
        if (path == "/api/preset/20ssb") { _radio.SetFreq(14200000); _radio.SetMode("USB"); return OK("preset", "20ssb"); }
        if (path == "/api/preset/15cw") { _radio.SetFreq(21030000); _radio.SetMode("CW"); return OK("preset", "15cw"); }
        if (path == "/api/preset/15ssb") { _radio.SetFreq(21300000); _radio.SetMode("USB"); return OK("preset", "15ssb"); }
        if (path == "/api/preset/10ssb") { _radio.SetFreq(28400000); _radio.SetMode("USB"); return OK("preset", "10ssb"); }

        // ===== LOCK =====
        if (path == "/api/lock/on") { _radio.SetLock(true); return OK("lock", 1); }
        if (path == "/api/lock/off") { _radio.SetLock(false); return OK("lock", 0); }

        return null;
    }

    private object SendFreqBuffer()
    {
        if (string.IsNullOrEmpty(_freqBuffer))
            return new { status = "error", message = "Buffer is empty" };

        long freqHz;
        if (_freqBuffer.Length <= 3)
        {
            long.TryParse(_freqBuffer, out var mhz);
            freqHz = mhz * 1_000_000;
        }
        else
        {
            long.TryParse(_freqBuffer[..^3], out var mhz);
            long.TryParse(_freqBuffer[^3..], out var khz);
            freqHz = mhz * 1_000_000 + khz * 1_000;
        }

        var mode = BandHelper.GetModeForFrequency(freqHz);
        _radio.SetMode(mode);
        _radio.SetFreq(freqHz);
        _freqBuffer = "";
        return new { status = "ok", freq_hz = freqHz, mode, cleared = true };
    }

    private static object OK() => new { status = "ok" };
    private static object OK(string key, object val) =>
        new Dictionary<string, object> { ["status"] = "ok", [key] = val };

    private static void WriteJson(HttpListenerResponse resp, object data)
    {
        resp.ContentType = "application/json";
        var json = JsonSerializer.Serialize(data);
        var buf = Encoding.UTF8.GetBytes(json);
        resp.ContentLength64 = buf.Length;
        resp.OutputStream.Write(buf);
        resp.Close();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }
}
