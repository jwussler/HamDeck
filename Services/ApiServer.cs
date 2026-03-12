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
/// HTTP REST API server compatible with Stream Deck and the original Python daemon.
/// Port 5001: Full control API (localhost only).
/// Port 5002: Read-only dashboard (safe to expose to the internet).
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
        "/api/status",
        "/api/status/full",
        "/api/health",
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
    };

    public ApiServer(RadioController radio, AudioRecorder recorder, Config config,
                     TgxlTuner tgxl, AmpTuner amp, KmtronicService? kmtronic = null,
                     DxClusterClient? cluster = null, SessionStats? stats = null,
                     AudioStreamer? streamer = null, AuthService? auth = null,
                     AudioTransmitter? txAudio = null, FlexKnobController? flexknob = null)
    {
        _radio = radio;
        _recorder = recorder;
        _config = config;
        _tgxl = tgxl;
        _amp = amp;
        _kmtronic = kmtronic;
        _cluster = cluster;
        _stats = stats;
        _streamer = streamer;
        _auth = auth;
        _txAudio = txAudio;
        _flexknob = flexknob;

        var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
        _wwwroot = Path.Combine(exeDir, "wwwroot");
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();

        // ===== PORT 5001: Full control API =====
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

        // ===== PORT 5002: Read-only dashboard =====
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
            try
            {
                _dashboardListener.Start();
                Logger.Info("API", "Dashboard listening on localhost:{0}", dashPort);
                Task.Run(() => ListenLoop(_dashboardListener, true, _cts.Token));
            }
            catch (Exception ex2)
            {
                Logger.Warn("API", "Dashboard listener failed: {0}", ex2.Message);
                _dashboardListener = null;
            }
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
            try
            {
                var ctx = await listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(ctx, readOnly, ct));
            }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { Logger.Debug("API", "Listener error: {0}", ex.Message); }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx, bool readOnly, CancellationToken ct)
    {
        var resp = ctx.Response;
        resp.Headers.Add("Access-Control-Allow-Origin", "*");
        resp.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        resp.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

        if (ctx.Request.HttpMethod == "OPTIONS") { resp.StatusCode = 200; resp.Close(); return; }

        var path = ctx.Request.Url?.AbsolutePath ?? "/";

        // ===== AUDIO STREAM WebSocket (dashboard port only, no auth required) =====
        if (readOnly && path == "/ws" && ctx.Request.IsWebSocketRequest && _streamer != null)
        {
            await _streamer.HandleWebSocketClient(ctx, ct);
            return;
        }

        // ===== TX AUDIO WebSocket (dashboard port, authenticated + can_transmit) =====
        if (readOnly && path == "/ws/tx" && ctx.Request.IsWebSocketRequest)
        {
            if (_txAudio == null)
            {
                Logger.Warn("API", "TX audio WebSocket requested but service not available");
                resp.StatusCode = 503;
                resp.Close();
                return;
            }

            var token = GetSessionToken(ctx);
            if (_auth != null && (!_auth.ValidateSession(token) || !_auth.CanTransmit(token)))
            {
                Logger.Warn("API", "TX audio WebSocket rejected — not authenticated or TX not permitted");
                try
                {
                    var wsCtx = await ctx.AcceptWebSocketAsync(null);
                    await wsCtx.WebSocket.CloseAsync(
                        WebSocketCloseStatus.PolicyViolation,
                        "Authentication required or transmit not permitted", CancellationToken.None);
                }
                catch { resp.StatusCode = 401; resp.Close(); }
                return;
            }

            Logger.Info("API", "TX audio WebSocket accepted");
            await _txAudio.HandleWebSocketClient(ctx, ct);
            return;
        }

        // ===== FLEX KNOB WebSocket (dashboard port — browser Web Serial bridge) =====
        if (readOnly && path == "/wsflexknob" && ctx.Request.IsWebSocketRequest && _flexknob != null)
        {
            await HandleFlexKnobWebSocket(ctx, ct);
            return;
        }

        // ===== AUDIO PLAYER PAGE (dashboard port only, no auth required) =====
        if (readOnly && path == "/audio" && _streamer != null)
        {
            resp.ContentType = "text/html; charset=utf-8";
            var html = Encoding.UTF8.GetBytes(_streamer.GetPlayerHtml());
            resp.ContentLength64 = html.Length;
            resp.OutputStream.Write(html);
            resp.Close();
            return;
        }

        if (path.StartsWith("/api/"))
        {
            var trimmed = path.TrimEnd('/');

            // ===== AUTH ENDPOINTS (port 5002 only) =====
            if (readOnly && trimmed == "/api/auth/login" && ctx.Request.HttpMethod == "POST")
            {
                await HandleLogin(ctx);
                return;
            }
            if (readOnly && trimmed == "/api/auth/logout")
            {
                HandleLogout(ctx);
                return;
            }
            if (readOnly && trimmed == "/api/auth/status")
            {
                var token = GetSessionToken(ctx);
                var valid = _auth?.ValidateSession(token) ?? false;
                var isAdmin = valid && (_auth?.IsAdmin(token) ?? false);
                var canTx = valid && (_auth?.CanTransmit(token) ?? false);
                var username = valid ? _auth?.GetUsername(token) : null;
                WriteJson(resp, new { status = "ok", authenticated = valid, is_admin = isAdmin, can_transmit = canTx, username, token = valid ? token : null });
                return;
            }
            if (readOnly && trimmed == "/api/auth/setup" && ctx.Request.HttpMethod == "POST")
            {
                await HandlePasswordSetup(ctx);
                return;
            }

            // ===== ADMIN ENDPOINTS (port 5002, authenticated + admin role) =====
            if (readOnly && trimmed.StartsWith("/api/admin/"))
            {
                var token = GetSessionToken(ctx);
                if (_auth == null || !_auth.IsAdmin(token))
                {
                    resp.StatusCode = 403;
                    WriteJson(resp, new { status = "error", message = "Admin access required" });
                    return;
                }

                var adminResult = await HandleAdminRoute(trimmed, ctx);
                if (adminResult != null) { WriteJson(resp, adminResult); return; }
            }

            // ===== PTT / TX PERMISSION GATE =====
            if (readOnly && (trimmed == "/api/ptt/on" || trimmed == "/api/ptt/off" ||
                             trimmed == "/api/ptt/key" || trimmed == "/api/ptt/unkey" ||
                             trimmed == "/api/ptt/toggle"))
            {
                var token = GetSessionToken(ctx);
                if (_auth != null && !_auth.CanTransmit(token))
                {
                    resp.StatusCode = 403;
                    WriteJson(resp, new { status = "error", message = "Transmit not permitted for this account" });
                    return;
                }
            }

            // ===== PORT 5002 AUTH GATE =====
            if (readOnly && !ReadOnlyRoutes.Contains(trimmed))
            {
                var token = GetSessionToken(ctx);
                if (_auth == null || !_auth.ValidateSession(token))
                {
                    resp.StatusCode = 401;
                    WriteJson(resp, new { status = "error", message = "Authentication required" });
                    return;
                }
            }

            object? result = Route(trimmed);
            WriteJson(resp, result ?? new { status = "error", message = "Unknown route", path });
            return;
        }

        if (TryServeFile(path, resp))
            return;

        if (readOnly)
            WriteJson(resp, new { service = "HamDeck Dashboard (read-only)", port = _config.APIPort + 1 });
        else
            WriteJson(resp, new { service = "HamDeck API (C#)", port = _config.APIPort, dashboard = Directory.Exists(_wwwroot) });
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

            if (ext != ".html")
                resp.Headers.Add("Cache-Control", "public, max-age=300");
            else
                resp.Headers.Add("Cache-Control", "no-cache");

            var bytes = File.ReadAllBytes(fullPath);
            resp.ContentLength64 = bytes.Length;
            resp.OutputStream.Write(bytes);
            resp.Close();
            return true;
        }
        catch (Exception ex)
        {
            Logger.Debug("API", "Static file error: {0}", ex.Message);
            return false;
        }
    }

    private object? Route(string path)
    {
        if (path == "/api/test") return new { ok = true, message = "API is working" };
        if (path == "/api/health") return new { status = "ok", service = "HamDeck API (C#)", version = "3.3", port = _config.APIPort, rig_connected = _radio.Connected, amp_tuning = _amp.IsActive, tgxl_tuning = _tgxl.IsActive, freq_buffer = _freqBuffer };

        if (path == "/api/status")
        {
            if (!_radio.Connected)
                return new { connected = false, amp_tuning = _amp.IsActive, tgxl_tuning = _tgxl.IsActive, freq_buffer = _freqBuffer };

            // Fast poll — only essential data (6 serial queries)
            return new
            {
                connected = true,
                freq = _radio.GetFreq(),
                mode = _radio.GetMode(),
                vfo = _radio.GetVFO(),
                power = _radio.GetPower(),
                tx = _radio.GetTXStatus(),
                split = _radio.GetSplit(),
                amp_tuning = _amp.IsActive,
                tgxl_tuning = _tgxl.IsActive,
                freq_buffer = _freqBuffer
            };
        }

        if (path == "/api/status/full")
        {
            if (!_radio.Connected)
                return new { connected = false };

            // Slow poll — all toggle states (adds ~14 serial queries)
            var rit = _radio.GetRIT();
            return new
            {
                ant = _radio.GetAntenna(),
                rxant = _radio.GetRxAntenna(),
                nb = _radio.GetNB(),
                nr = _radio.GetNR(),
                notch = _radio.GetNotch(),
                @lock = _radio.GetLock(),
                preamp = _radio.GetPreamp(),
                att = _radio.GetATT(),
                agc = _radio.GetAGC(),
                vox = _radio.GetVOX(),
                comp = _radio.GetComp(),
                rit = rit.On,
                rit_offset = rit.Offset,
                xit = _radio.GetXIT(),
                rxant_km = _kmtronic?.ActiveAntenna ?? 0
            };
        }

        if (path == "/api/mode/usb") { _radio.SetMode("USB"); return OK("mode", "USB"); }
        if (path == "/api/mode/lsb") { _radio.SetMode("LSB"); return OK("mode", "LSB"); }
        if (path == "/api/mode/cw") { _radio.SetMode("CW"); return OK("mode", "CW"); }
        if (path == "/api/mode/am") { _radio.SetMode("AM"); return OK("mode", "AM"); }
        if (path == "/api/mode/fm") { _radio.SetMode("FM"); return OK("mode", "FM"); }
        if (path == "/api/mode/data") { _radio.SetMode("DATA-U"); return OK("mode", "DATA-U"); }
        if (path.StartsWith("/api/mode/")) { var m = path["/api/mode/".Length..].ToUpper(); _radio.SetMode(m); return OK("mode", m); }

        foreach (var (key, freq) in BandHelper.BandFrequencies)
        {
            if (path == $"/api/band/{key}")
            {
                var mode = BandHelper.GetModeForFrequency(freq);
                _radio.SetMode(mode); _radio.SetFreq(freq);
                return new { status = "ok", band = key, freq, mode };
            }
        }

        if (path == "/api/vfo/a") { _radio.SetVFO("A"); return OK("vfo", "A"); }
        if (path == "/api/vfo/b") { _radio.SetVFO("B"); return OK("vfo", "B"); }
        if (path == "/api/vfo/swap") { _radio.SwapVFO(); return OK("action", "swap"); }
        if (path == "/api/vfo-copy/a2b") { _radio.CopyVFO("A", "B"); return OK("action", "a2b"); }
        if (path == "/api/vfo-copy/b2a") { _radio.CopyVFO("B", "A"); return OK("action", "b2a"); }

        if (path == "/api/split/on") { _radio.SetSplit(true); return OK("split", 1); }
        if (path == "/api/split/off") { _radio.SetSplit(false); return OK("split", 0); }
        if (path == "/api/split/toggle") { var c = _radio.GetSplit(); _radio.SetSplit(!c); return OK("split", !c); }
        if (path == "/api/quick-split") { _radio.QuickSplit(5000); return OK("offset", 5000); }

        if (path is "/api/ptt/on" or "/api/ptt/key") { _radio.SetPTT(true); return OK("ptt", 1); }
        if (path is "/api/ptt/off" or "/api/ptt/unkey") { _radio.SetPTT(false); return OK("ptt", 0); }
        if (path == "/api/ptt/toggle") { var c = _radio.GetTXStatus(); _radio.SetPTT(!c); return OK("ptt", !c); }

        if (path == "/api/power/qrp") { _radio.SetPower(5); return new { status = "ok", power = "qrp", watts = 5 }; }
        if (path == "/api/power/low") { _radio.SetPower(25); return new { status = "ok", power = "low", watts = 25 }; }
        if (path == "/api/power/mid") { _radio.SetPower(50); return new { status = "ok", power = "mid", watts = 50 }; }
        if (path == "/api/power/high") { _radio.SetPower(100); return new { status = "ok", power = "high", watts = 100 }; }
        if (path == "/api/power/max") { _radio.SetPower(200); return new { status = "ok", power = "max", watts = 200 }; }
        if (path.StartsWith("/api/power/set/") && int.TryParse(path["/api/power/set/".Length..], out var pw))
        { _radio.SetPower(pw); return OK("power", pw); }

        if (path == "/api/freq") return new { freq = _radio.GetFreq() };
        if (path.StartsWith("/api/freq/set/") && long.TryParse(path["/api/freq/set/".Length..], out var fq))
        {
            var mode = BandHelper.GetModeForFrequency(fq); _radio.SetMode(mode); _radio.SetFreq(fq);
            return new { status = "ok", freq = fq, mode };
        }

        if (path.StartsWith("/api/freq/digit/") && path.Length > "/api/freq/digit/".Length)
        { var d = path["/api/freq/digit/".Length..]; _freqBuffer += d; return OK("buffer", _freqBuffer); }
        if (path == "/api/freq/clear") { _freqBuffer = ""; return OK("buffer", ""); }
        if (path == "/api/freq/backspace") { if (_freqBuffer.Length > 0) _freqBuffer = _freqBuffer[..^1]; return OK("buffer", _freqBuffer); }
        if (path == "/api/freq/get") return new { status = "ok", buffer = _freqBuffer, length = _freqBuffer.Length };
        if (path == "/api/freq/send") return SendFreqBuffer();

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

        if (path == "/api/record/status") return _recorder.GetStatus();
        if (path == "/api/record/start") { _recorder.Start(); return OK("recording", true); }
        if (path == "/api/record/stop") { var fn = _recorder.Stop(); return new { status = "ok", filename = fn }; }
        if (path is "/api/record/toggle" or "/api/record/toggle/stereo")
        {
            if (_recorder.IsRecording) { var fn = _recorder.Stop(); return new { status = "ok", action = "stopped", filename = fn }; }
            _recorder.Start(); return new { status = "ok", action = "started" };
        }
        if (path == "/api/record/replay") { var fn = _recorder.SaveReplay(); return new { status = "ok", filename = fn }; }

        if (path == "/api/volume/get") { var v = _radio.GetAFGain(); return new { status = "ok", volume = v * 100 / 255, raw = v }; }
        if (path == "/api/volume/up") { _radio.SetAFGain(_radio.GetAFGain() + 13); return OK(); }
        if (path == "/api/volume/down") { _radio.SetAFGain(_radio.GetAFGain() - 13); return OK(); }
        if (path.StartsWith("/api/volume/set/") && int.TryParse(path["/api/volume/set/".Length..], out var vp))
        { _radio.SetAFGain(vp * 255 / 100); return OK("volume", vp); }
        if (path == "/api/mute/on") { _radio.SetAFGain(0); return OK("mute", 1); }
        if (path == "/api/mute/off") { _radio.SetAFGain(128); return OK("mute", 0); }
        if (path == "/api/mute/toggle") { if (_radio.GetAFGain() > 0) { _radio.SetAFGain(0); return OK("mute", 1); } _radio.SetAFGain(128); return OK("mute", 0); }

        // Sub-band mute (AG1)
        if (path == "/api/mute-sub/on") { _radio.SetSubAFGain(0); return OK("mute_sub", 1); }
        if (path == "/api/mute-sub/off") { _radio.SetSubAFGain(128); return OK("mute_sub", 0); }
        if (path == "/api/mute-sub/toggle") { if (_radio.GetSubAFGain() > 0) { _radio.SetSubAFGain(0); return OK("mute_sub", 1); } _radio.SetSubAFGain(128); return OK("mute_sub", 0); }

        // Mute all (main + sub)
        if (path == "/api/mute-all/on") { _radio.SetAFGain(0); _radio.SetSubAFGain(0); return OK("mute_all", 1); }
        if (path == "/api/mute-all/off") { _radio.SetAFGain(128); _radio.SetSubAFGain(128); return OK("mute_all", 0); }
        if (path == "/api/mute-all/toggle") {
            bool mainMuted = _radio.GetAFGain() == 0;
            bool subMuted = _radio.GetSubAFGain() == 0;
            if (mainMuted && subMuted) { _radio.SetAFGain(128); _radio.SetSubAFGain(128); return OK("mute_all", 0); }
            _radio.SetAFGain(0); _radio.SetSubAFGain(0); return OK("mute_all", 1);
        }

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

        if (path == "/api/preamp/on") { _radio.SetPreamp(true); return OK("preamp", 1); }
        if (path == "/api/preamp/off") { _radio.SetPreamp(false); return OK("preamp", 0); }
        if (path == "/api/preamp/cycle") { _radio.CyclePreamp(); return OK("action", "cycle"); }
        if (path == "/api/att/on") { _radio.SetATT(true); return OK("att", 1); }
        if (path == "/api/att/off") { _radio.SetATT(false); return OK("att", 0); }
        if (path == "/api/att/toggle") { var c = _radio.GetATT(); _radio.SetATT(!c); return OK("att", !c); }

        if (path == "/api/agc/fast") { _radio.SetAGC("FAST"); return OK("agc", "FAST"); }
        if (path == "/api/agc/mid") { _radio.SetAGC("MID"); return OK("agc", "MID"); }
        if (path == "/api/agc/slow") { _radio.SetAGC("SLOW"); return OK("agc", "SLOW"); }
        if (path == "/api/agc/off") { _radio.SetAGC("OFF"); return OK("agc", "OFF"); }
        if (path == "/api/agc/auto") { _radio.SetAGC("AUTO"); return OK("agc", "AUTO"); }
        if (path == "/api/agc/cycle") {
            var cur = _radio.GetAGC();
            var next = cur switch { "FAST" => "MID", "MID" => "SLOW", "SLOW" => "AUTO", "AUTO" => "OFF", _ => "FAST" };
            _radio.SetAGC(next); return OK("agc", next);
        }

        if (path == "/api/vox/on") { _radio.SetVOX(true); return OK("vox", 1); }
        if (path == "/api/vox/off") { _radio.SetVOX(false); return OK("vox", 0); }
        if (path == "/api/vox/toggle") { var c = _radio.GetVOX(); _radio.SetVOX(!c); return OK("vox", !c); }
        if (path == "/api/comp/on") { _radio.SetComp(true); return OK("comp", 1); }
        if (path == "/api/comp/off") { _radio.SetComp(false); return OK("comp", 0); }
        if (path == "/api/comp/toggle") { var c = _radio.GetComp(); _radio.SetComp(!c); return OK("comp", !c); }

        if (path == "/api/rit/on") { _radio.SetRIT(true); return OK("rit", 1); }
        if (path == "/api/rit/off") { _radio.SetRIT(false); return OK("rit", 0); }
        if (path == "/api/rit/toggle") { var (on, _) = _radio.GetRIT(); _radio.SetRIT(!on); return OK("rit", !on); }
        if (path == "/api/rit/up") { var (_, o) = _radio.GetRIT(); _radio.SetRITOffset(o + 100); return OK("action", "up"); }
        if (path == "/api/rit/down") { var (_, o) = _radio.GetRIT(); _radio.SetRITOffset(o - 100); return OK("action", "down"); }
        if (path == "/api/rit/clear") { _radio.ClearRIT(); return OK("action", "clear"); }
        if (path == "/api/xit/on") { _radio.SetXIT(true); return OK("xit", 1); }
        if (path == "/api/xit/off") { _radio.SetXIT(false); return OK("xit", 0); }
        if (path == "/api/xit/toggle") { var c = _radio.GetXIT(); _radio.SetXIT(!c); return OK("xit", !c); }

        if (path == "/api/cw-speed/get") return new { status = "ok", wpm = _radio.GetCWSpeed() };
        if (path == "/api/cw-speed/up") { var w = _radio.GetCWSpeed(); _radio.SetCWSpeed(w + 2); return OK("wpm", w + 2); }
        if (path == "/api/cw-speed/down") { var w = _radio.GetCWSpeed(); _radio.SetCWSpeed(w - 2); return OK("wpm", w - 2); }
        if (path.StartsWith("/api/cw-speed/set/") && int.TryParse(path["/api/cw-speed/set/".Length..], out var wpm))
        { _radio.SetCWSpeed(wpm); return OK("wpm", wpm); }

        if (path == "/api/meters") return new { status = "ok", s_meter = _radio.GetSMeter(), swr = _radio.GetSWR(), alc = _radio.GetALC(), power = _radio.GetPowerMeter() };

        if (path.StartsWith("/api/memory/recall/") && int.TryParse(path["/api/memory/recall/".Length..], out var mem))
        { _radio.RecallMemory(mem); return OK("memory", mem); }

        if (path == "/api/width/narrow") { _radio.SetWidth(6); return new { status = "ok", width = "narrow", hz = 1800 }; }
        if (path == "/api/width/medium") { _radio.SetWidth(10); return new { status = "ok", width = "medium", hz = 2400 }; }
        if (path == "/api/width/wide") { _radio.SetWidth(14); return new { status = "ok", width = "wide", hz = 3000 }; }

        if (path == "/api/preset/40cw") { _radio.SetFreq(7030000); _radio.SetMode("CW"); return OK("preset", "40cw"); }
        if (path == "/api/preset/40ssb") { _radio.SetFreq(7200000); _radio.SetMode("LSB"); return OK("preset", "40ssb"); }
        if (path == "/api/preset/20cw") { _radio.SetFreq(14030000); _radio.SetMode("CW"); return OK("preset", "20cw"); }
        if (path == "/api/preset/20ssb") { _radio.SetFreq(14200000); _radio.SetMode("USB"); return OK("preset", "20ssb"); }
        if (path == "/api/preset/15cw") { _radio.SetFreq(21030000); _radio.SetMode("CW"); return OK("preset", "15cw"); }
        if (path == "/api/preset/15ssb") { _radio.SetFreq(21300000); _radio.SetMode("USB"); return OK("preset", "15ssb"); }
        if (path == "/api/preset/10ssb") { _radio.SetFreq(28400000); _radio.SetMode("USB"); return OK("preset", "10ssb"); }

        if (path == "/api/lock/on") { _radio.SetLock(true); return OK("lock", 1); }
        if (path == "/api/lock/off") { _radio.SetLock(false); return OK("lock", 0); }

        if (path == "/api/ant/1") { _radio.SetAntenna(1); return OK("ant", 1); }
        if (path == "/api/ant/2") { _radio.SetAntenna(2); return OK("ant", 2); }
        if (path == "/api/ant/3") { _radio.SetAntenna(3); return OK("ant", 3); }
        if (path == "/api/ant/toggle") { _radio.ToggleAntenna(); return OK("ant", _radio.GetAntenna()); }
        if (path == "/api/ant/get") return new { status = "ok", ant = _radio.GetAntenna() };

        if (path == "/api/ant/rx/on") { _radio.SetRxAntenna(true); return OK("rxant", 1); }
        if (path == "/api/ant/rx/off") { _radio.SetRxAntenna(false); return OK("rxant", 0); }
        if (path == "/api/ant/rx/toggle") { var c = _radio.GetRxAntenna(); _radio.SetRxAntenna(!c); return OK("rxant", !c); }
        if (path == "/api/ant/rx/get") return new { status = "ok", rxant = _radio.GetRxAntenna() };

        if (path.StartsWith("/api/rxant/") && _kmtronic != null)
        {
            var seg = path["/api/rxant/".Length..];
            if (seg == "get") return new { status = "ok", rxant = _kmtronic.ActiveAntenna };
            if (int.TryParse(seg, out var rxant) && rxant >= 1 && rxant <= 4)
            { _kmtronic.SetAntenna(rxant); return OK("rxant", rxant); }
        }

        if (path == "/api/cluster/spots" && _cluster != null)
        {
            var spots = _cluster.Spots.Select(s => new
            {
                freq_khz = s.FreqKHz,
                freq_hz = s.FreqHz,
                dx_call = s.Spotted,
                spotter = s.Spotter,
                comment = s.Message,
                time = s.Time.ToString("o"),
                band = s.BandName,
                mode = s.Mode,
                entity = s.Entity,
                flag = s.Flag
            }).ToList();
            return new { status = "ok", spots, count = spots.Count };
        }

        if (path == "/api/session" && _stats != null)
        {
            return new
            {
                status = "ok",
                session_duration = _stats.SessionDuration,
                qsy_count = _stats.QSYCount,
                tx_count = _stats.PTTCount,
                tx_time = _stats.TXTimeDisplay,
                tx_seconds = (int)_stats.TotalTXTime.TotalSeconds,
                qso_count = _stats.QSOCount
            };
        }

        if (path == "/api/tx-audio/status")
        {
            return new
            {
                status = "ok",
                available = _txAudio != null,
                active = _txAudio?.IsActive ?? false,
                client_connected = _txAudio?.HasClient ?? false
            };
        }

        if (path == "/api/tx-audio/devices")
        {
            var devices = AudioTransmitter.ListDevices();
            return new
            {
                status = "ok",
                devices = devices.Select(d => new { index = d.Index, name = d.Name }).ToList(),
                current = _config.TxAudioDevice
            };
        }

        // Remote TX mode — switches radio between MIC and USB audio source
        if (path == "/api/remote-tx/on")
        {
            _radio.EnableRemoteTx();
            return new { status = "ok", remote_tx = true, message = "SSB MOD SOURCE=REAR, REAR SELECT=USB" };
        }
        if (path == "/api/remote-tx/off")
        {
            _radio.DisableRemoteTx();
            return new { status = "ok", remote_tx = false, message = "SSB MOD SOURCE=MIC" };
        }
        if (path == "/api/remote-tx/status")
        {
            return new
            {
                status = "ok",
                mod_source_rear = _radio.GetSSBModSourceRear(),
                rear_select_usb = _radio.GetRearSelectUSB(),
                rport_gain = _radio.GetRPortGain()
            };
        }
        if (path.StartsWith("/api/remote-tx/gain/") &&
            int.TryParse(path["/api/remote-tx/gain/".Length..], out var rGain))
        {
            _radio.SetRPortGain(rGain);
            return OK("rport_gain", rGain);
        }

        // SSB OUT LEVEL — controls RX audio volume to USB codec
        if (path == "/api/ssb-out-level/get")
        {
            return new { status = "ok", level = _radio.GetSSBOutLevel() };
        }
        if (path.StartsWith("/api/ssb-out-level/set/") &&
            int.TryParse(path["/api/ssb-out-level/set/".Length..], out var outLevel))
        {
            _radio.SetSSBOutLevel(outLevel);
            return OK("ssb_out_level", outLevel);
        }

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

    // ===== AUTH HELPERS =====

    private string? GetSessionToken(HttpListenerContext ctx)
    {
        // Check cookie first
        var cookie = ctx.Request.Cookies["hamdeck_session"];
        if (cookie != null) return cookie.Value;

        // Check Authorization header
        var authHeader = ctx.Request.Headers["Authorization"];
        if (authHeader != null && authHeader.StartsWith("Bearer "))
            return authHeader[7..];

        // Check URL query parameter (for WebSocket through Cloudflare)
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
            var body = await reader.ReadToEndAsync();
            var login = JsonSerializer.Deserialize<LoginRequest>(body);

            if (login == null || _auth == null)
            {
                resp.StatusCode = 400;
                WriteJson(resp, new { status = "error", message = "Invalid request" });
                return;
            }

            var token = _auth.Login(login.Username ?? "", login.Password ?? "");
            if (token == null)
            {
                await Task.Delay(500);
                resp.StatusCode = 401;
                WriteJson(resp, new { status = "error", message = "Invalid credentials" });
                return;
            }

            // Admin-only lockdown: reject non-admin logins after credentials check
            if (_config.AdminOnlyLogin && !_auth.IsAdmin(token))
            {
                _auth.Logout(token); // clean up the session we just created
                await Task.Delay(300);
                resp.StatusCode = 403;
                WriteJson(resp, new { status = "error", message = "Login is currently restricted to administrators" });
                return;
            }

            var maxAge = (_config.WebSessionTimeout > 0 ? _config.WebSessionTimeout : 480) * 60;
            resp.Headers.Add("Set-Cookie",
                $"hamdeck_session={token}; Path=/; HttpOnly; SameSite=Lax; Max-Age={maxAge}");
            WriteJson(resp, new { status = "ok", message = "Login successful" });
        }
        catch (Exception ex)
        {
            Logger.Warn("AUTH", "Login error: {0}", ex.Message);
            resp.StatusCode = 500;
            WriteJson(resp, new { status = "error", message = "Internal error" });
        }
    }

    private void HandleLogout(HttpListenerContext ctx)
    {
        var token = GetSessionToken(ctx);
        _auth?.Logout(token);
        ctx.Response.Headers.Add("Set-Cookie",
            "hamdeck_session=; Path=/; HttpOnly; Max-Age=0");
        WriteJson(ctx.Response, new { status = "ok", message = "Logged out" });
    }

    private async Task HandlePasswordSetup(HttpListenerContext ctx)
    {
        var resp = ctx.Response;
        if (_auth != null && _auth.IsConfigured)
        {
            resp.StatusCode = 403;
            WriteJson(resp, new { status = "error", message = "Password already configured. Use admin panel to add users." });
            return;
        }
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var setup = JsonSerializer.Deserialize<SetupRequest>(body);

            if (setup == null || string.IsNullOrWhiteSpace(setup.Password) || setup.Password.Length < 6)
            {
                resp.StatusCode = 400;
                WriteJson(resp, new { status = "error", message = "Password must be at least 6 characters" });
                return;
            }

            var username = setup.Username?.Trim().ToLower() ?? "wa0o";
            var hash = AuthService.HashPassword(setup.Password);

            if (_auth == null)
                _auth = new AuthService(_config.WebSessionTimeout);

            _auth.AddUser(username, hash, isAdmin: true, canTransmit: true); // First user is admin

            _config.WebUsers.Add(new Config.WebUser { Username = username, PasswordHash = hash, IsAdmin = true, CanTransmit = true });
            _config.WebPasswordHash = hash;
            _config.WebUsername = username;
            _config.Save();

            Logger.Info("AUTH", "Initial admin user '{0}' created", username);
            WriteJson(resp, new { status = "ok", message = "Admin account created. Please log in." });
        }
        catch (Exception ex)
        {
            resp.StatusCode = 500;
            WriteJson(resp, new { status = "error", message = ex.Message });
        }
    }

    // ===== ADMIN HANDLERS =====

    private async Task<object?> HandleAdminRoute(string path, HttpListenerContext ctx)
    {
        // List users
        if (path == "/api/admin/users")
            return new { status = "ok", users = _auth!.GetUsers() };

        // List active sessions
        if (path == "/api/admin/sessions")
            return new { status = "ok", sessions = _auth!.GetActiveSessions() };

        // Add user
        if (path == "/api/admin/user/add" && ctx.Request.HttpMethod == "POST")
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var req = JsonSerializer.Deserialize<AdminUserRequest>(body);

            if (req == null || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 6)
                return new { status = "error", message = "Username and password (6+ chars) required" };

            var hash = AuthService.HashPassword(req.Password);
            _auth!.AddUser(req.Username, hash, req.IsAdmin, req.CanTransmit);

            _config.WebUsers.Add(new Config.WebUser
            {
                Username = req.Username.Trim().ToLower(),
                PasswordHash = hash,
                IsAdmin = req.IsAdmin,
                CanTransmit = req.CanTransmit
            });
            _config.Save();

            return new { status = "ok", message = $"User '{req.Username}' added" };
        }

        // Remove user
        if (path.StartsWith("/api/admin/user/remove/"))
        {
            var username = path["/api/admin/user/remove/".Length..];
            if (_auth!.RemoveUser(username))
            {
                _config.WebUsers.RemoveAll(u => u.Username.Equals(username.Trim(), StringComparison.OrdinalIgnoreCase));
                _config.Save();
                return new { status = "ok", message = $"User '{username}' removed" };
            }
            return new { status = "error", message = "User not found" };
        }

        // Change password
        if (path == "/api/admin/user/password" && ctx.Request.HttpMethod == "POST")
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var req = JsonSerializer.Deserialize<AdminPasswordRequest>(body);

            if (req == null || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 6)
                return new { status = "error", message = "Username and new password (6+ chars) required" };

            var hash = AuthService.HashPassword(req.NewPassword);
            if (_auth!.ChangePassword(req.Username, hash))
            {
                var user = _config.WebUsers.Find(u => u.Username.Equals(req.Username.Trim(), StringComparison.OrdinalIgnoreCase));
                if (user != null) user.PasswordHash = hash;
                _config.Save();
                return new { status = "ok", message = $"Password changed for '{req.Username}'" };
            }
            return new { status = "error", message = "User not found" };
        }

        // Kill user sessions (force disconnect)
        if (path.StartsWith("/api/admin/kick/"))
        {
            var username = path["/api/admin/kick/".Length..];
            var count = _auth!.KillUserSessions(username);
            return new { status = "ok", kicked = count, message = $"Killed {count} sessions for '{username}'" };
        }

        // ===== LOCKDOWN: admin-only login toggle =====
        if (path == "/api/admin/lockdown/on")
        {
            _config.AdminOnlyLogin = true;
            _config.Save();
            Logger.Info("AUTH", "Admin-only lockdown ENABLED");
            return new { status = "ok", admin_only_login = true, message = "Login restricted to admins" };
        }
        if (path == "/api/admin/lockdown/off")
        {
            _config.AdminOnlyLogin = false;
            _config.Save();
            Logger.Info("AUTH", "Admin-only lockdown DISABLED");
            return new { status = "ok", admin_only_login = false, message = "All users may log in" };
        }
        if (path == "/api/admin/lockdown/status")
        {
            return new { status = "ok", admin_only_login = _config.AdminOnlyLogin };
        }

        // ===== PER-USER TX PERMISSION =====
        // PUT /api/admin/user/tx/enable/{username}
        // PUT /api/admin/user/tx/disable/{username}
        if (path.StartsWith("/api/admin/user/tx/"))
        {
            var remainder = path["/api/admin/user/tx/".Length..]; // e.g. "enable/wa0o" or "disable/wa0o"
            var slash = remainder.IndexOf('/');
            if (slash > 0)
            {
                var action = remainder[..slash];   // "enable" or "disable"
                var username = remainder[(slash + 1)..];
                bool allow = action == "enable";

                if (_auth!.SetUserCanTransmit(username, allow))
                {
                    var cfgUser = _config.WebUsers.Find(u =>
                        u.Username.Equals(username.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (cfgUser != null) cfgUser.CanTransmit = allow;
                    _config.Save();
                    return new { status = "ok", username, can_transmit = allow };
                }
                return new { status = "error", message = $"User '{username}' not found" };
            }
        }

        // Release mic (disconnect TX audio client)
        if (path == "/api/admin/mic/release")
        {
            _txAudio?.Stop();
            return new { status = "ok", message = "TX audio client disconnected" };
        }

        // Radio status
        if (path == "/api/admin/radio")
        {
            return new
            {
                status = "ok",
                connected = _radio.Connected,
                port = _radio.PortName,
                freq = _radio.LastFrequency,
                mode = _radio.LastMode,
                tx = _radio.LastTXState,
                tx_audio_active = _txAudio?.IsActive ?? false,
                tx_audio_client = _txAudio?.HasClient ?? false,
                proxy_active = _radio.ProxyIsActive,
                sessions = _auth!.ActiveSessionCount,
                admin_only_login = _config.AdminOnlyLogin
            };
        }

        // TX audio devices
        if (path == "/api/admin/tx-devices")
        {
            var devices = AudioTransmitter.ListDevices();
            return new
            {
                status = "ok",
                devices = devices.Select(d => new { index = d.Index, name = d.Name }).ToList(),
                current = _config.TxAudioDevice
            };
        }

        // Set RPORT gain
        if (path.StartsWith("/api/admin/rport-gain/") &&
            int.TryParse(path["/api/admin/rport-gain/".Length..], out var gain))
        {
            _radio.SetRPortGain(gain);
            return new { status = "ok", rport_gain = gain };
        }

        return null;
    }

    private class LoginRequest
    {
        [JsonPropertyName("username")] public string? Username { get; set; }
        [JsonPropertyName("password")] public string? Password { get; set; }
    }

    private class SetupRequest
    {
        [JsonPropertyName("username")] public string? Username { get; set; }
        [JsonPropertyName("password")] public string? Password { get; set; }
    }

    private class AdminUserRequest
    {
        [JsonPropertyName("username")] public string? Username { get; set; }
        [JsonPropertyName("password")] public string? Password { get; set; }
        [JsonPropertyName("is_admin")] public bool IsAdmin { get; set; }
        [JsonPropertyName("can_transmit")] public bool CanTransmit { get; set; } = true;
    }

    private class AdminPasswordRequest
    {
        [JsonPropertyName("username")] public string? Username { get; set; }
        [JsonPropertyName("new_password")] public string? NewPassword { get; set; }
    }

    private async Task HandleFlexKnobWebSocket(HttpListenerContext ctx, CancellationToken ct)
    {
        HttpListenerWebSocketContext? wsCtx = null;
        try
        {
            wsCtx = await ctx.AcceptWebSocketAsync(null);
        }
        catch (Exception ex)
        {
            Logger.Warn("FLEXKNOB-WS", "WebSocket upgrade failed: {0}", ex.Message);
            ctx.Response.StatusCode = 500;
            ctx.Response.Close();
            return;
        }

        var ws = wsCtx.WebSocket;
        Logger.Info("FLEXKNOB-WS", "Browser client connected from {0}", ctx.Request.RemoteEndPoint);

        var sendLock = new SemaphoreSlim(1, 1);

        async Task SafeSend(object data)
        {
            if (ws.State != WebSocketState.Open) return;
            var buf = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data));
            await sendLock.WaitAsync(ct);
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.SendAsync(buf, WebSocketMessageType.Text, true, ct);
            }
            catch { }
            finally { sendLock.Release(); }
        }

        Action<string> onMode   = mode   => _ = SafeSend(new { type = "mode",   mode, step = _flexknob!.StepDisplay });
        Action<string> onAction = action => _ = SafeSend(new { type = "action", action });

        _flexknob!.OnModeChanged += onMode;
        _flexknob!.OnAction      += onAction;

        await SafeSend(new
        {
            type         = "state",
            mode         = _flexknob.ModeName,
            step         = _flexknob.StepDisplay,
            hw_connected = _flexknob.IsConnected
        });

        try
        {
            var buf = new byte[4096];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                try { result = await ws.ReceiveAsync(buf, ct); }
                catch { break; }

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
                        if (!string.IsNullOrWhiteSpace(cmd))
                        {
                            Logger.Debug("FLEXKNOB-WS", "Inject: {0}", cmd);
                            _flexknob.InjectCommand(cmd!);
                        }
                    }
                    else if (msgType == "ping")
                    {
                        await SafeSend(new { type = "pong" });
                    }
                }
                catch (JsonException ex)
                {
                    Logger.Warn("FLEXKNOB-WS", "Bad JSON: {0}", ex.Message);
                }
            }
        }
        finally
        {
            _flexknob.OnModeChanged -= onMode;
            _flexknob.OnAction      -= onAction;

            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { }

            Logger.Info("FLEXKNOB-WS", "Browser client disconnected");
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        try { _dashboardListener?.Stop(); } catch { }
        _listener = null;
        _dashboardListener = null;
    }
}
