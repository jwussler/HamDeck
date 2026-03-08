using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HamDeck.Models;

namespace HamDeck.Services;

/// <summary>
/// WaveLogGate-compatible server: HTTP on 54321 (QSY commands from bandmap),
/// WebSocket on 54322 (status broadcasts), and direct Wavelog API posting.
/// </summary>
public class WaveLogServer : IDisposable
{
    private readonly RadioController _radio;
    private readonly Config _config;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly List<WebSocket> _clients = new();
    private readonly object _clientLock = new();
    private CancellationTokenSource? _cts;

    private long _lastFreq;
    private string _lastMode = "";
    private int _lastPower;

    // WARNING FIX: Cache TX state here instead of calling _radio.GetTXStatus() from
    // inside BroadcastToAll()/SendStatus(). GetTXStatus() issues a TX; serial query —
    // calling it per WebSocket client during a broadcast compounds serial bus traffic
    // and competes with MainWindow's UpdateTick for _radio's lock.
    // UpdateLoop updates _lastTX from _radio.LastTXState (set by the UI poll cycle).
    private bool _lastTX;

    public WaveLogServer(RadioController radio, Config config)
    {
        _radio = radio;
        _config = config;
    }

    public void Start()
    {
        if (!_config.WavelogEnabled)
        {
            Logger.Info("WAVELOG", "Disabled in settings");
            return;
        }

        _cts = new CancellationTokenSource();
        Task.Run(() => RunHttpServer(_cts.Token));
        Task.Run(() => RunWebSocketServer(_cts.Token));
        Task.Run(() => UpdateLoop(_cts.Token));

        Logger.Info("WAVELOG", "Servers started (HTTP:54321, WS:54322)");
        if (!string.IsNullOrEmpty(_config.WavelogURL))
            Logger.Info("WAVELOG", "API updates enabled: {0}/api/radio", _config.WavelogURL);
    }

    private async Task RunHttpServer(CancellationToken ct)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add("http://+:54321/");
        try { listener.Start(); }
        catch
        {
            listener = new HttpListener();
            listener.Prefixes.Add("http://localhost:54321/");
            listener.Start();
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ctx = await listener.GetContextAsync();
                HandleWavelogHttp(ctx);
            }
            catch (ObjectDisposedException) { break; }
            catch { }
        }
    }

    private void HandleWavelogHttp(HttpListenerContext ctx)
    {
        var resp = ctx.Response;
        resp.Headers.Add("Access-Control-Allow-Origin", "*");
        resp.Headers.Add("Access-Control-Allow-Private-Network", "true");

        if (ctx.Request.HttpMethod == "OPTIONS") { resp.StatusCode = 200; resp.Close(); return; }

        var path = ctx.Request.Url?.AbsolutePath?.Trim('/') ?? "";
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length >= 1 && long.TryParse(parts[0], out var freq))
        {
            _radio.SetFreq(freq);
            Logger.Info("WAVELOG", "QSY to {0} Hz", freq);
            if (parts.Length >= 2) { _radio.SetMode(parts[1].ToUpper()); Logger.Info("WAVELOG", "Mode: {0}", parts[1].ToUpper()); }

            WriteJson(resp, new { status = "ok", freq, mode = _radio.GetMode() });
            return;
        }

        WriteJson(resp, new { status = "ok", service = "HamDeck WaveLog Bridge",
            frequency = _radio.LastFrequency, mode = _radio.LastMode });
    }

    private async Task RunWebSocketServer(CancellationToken ct)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add("http://+:54322/");
        try { listener.Start(); }
        catch
        {
            listener = new HttpListener();
            listener.Prefixes.Add("http://localhost:54322/");
            listener.Start();
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ctx = await listener.GetContextAsync();
                if (ctx.Request.IsWebSocketRequest)
                {
                    var wsCtx = await ctx.AcceptWebSocketAsync(null);
                    _ = Task.Run(() => HandleWebSocketClient(wsCtx.WebSocket, ct));
                }
                else
                {
                    ctx.Response.StatusCode = 400;
                    ctx.Response.Close();
                }
            }
            catch (ObjectDisposedException) { break; }
            catch { }
        }
    }

    private async Task HandleWebSocketClient(WebSocket ws, CancellationToken ct)
    {
        lock (_clientLock) _clients.Add(ws);
        Logger.Info("WAVELOG", "WebSocket client connected");

        try
        {
            await SendStatus(ws);
            var buf = new byte[1024];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                await ws.ReceiveAsync(buf, ct);
            }
        }
        catch { }
        finally
        {
            lock (_clientLock) _clients.Remove(ws);
            Logger.Info("WAVELOG", "WebSocket client disconnected");
        }
    }

    private async Task UpdateLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(500, ct);
            if (!_radio.Connected) continue;

            // Use cached values — MainWindow's UpdateTick already calls GetFreq/GetMode/GetTXStatus
            // every 200ms so these are always fresh. Avoids doubling serial bus traffic.
            var freq  = _radio.LastFrequency;
            var mode  = _radio.LastMode;
            var power = _radio.LastPower;
            // LastTXState is updated by GetTXStatus() in the UI poll cycle — no extra serial hit needed.
            var tx    = _radio.LastTXState;

            if (freq != _lastFreq || mode != _lastMode || power != _lastPower || tx != _lastTX)
            {
                _lastFreq  = freq;
                _lastMode  = mode;
                _lastPower = power;
                _lastTX    = tx;
                await BroadcastToAll();
                await PostToWavelog(freq, mode, power);
            }
        }
    }

    private async Task PostToWavelog(long freq, string mode, int power)
    {
        if (string.IsNullOrEmpty(_config.WavelogURL) || string.IsNullOrEmpty(_config.WavelogAPIKey)) return;

        try
        {
            var apiUrl = _config.WavelogURL.TrimEnd('/') + "/api/radio";
            var payload = new Dictionary<string, object>
            {
                ["key"]       = _config.WavelogAPIKey,
                ["radio"]     = "HamDeck",
                ["frequency"] = freq,
                ["mode"]      = mode,
                ["timestamp"] = DateTime.UtcNow.ToString("yyyy/MM/dd HH:mm")
            };
            if (power > 0) payload["power"] = power;

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(apiUrl, content);

            if (resp.IsSuccessStatusCode)
                Logger.Debug("WAVELOG", "Updated freq={0} mode={1} power={2}", freq, mode, power);
        }
        catch (Exception ex) { Logger.Debug("WAVELOG", "API error: {0}", ex.Message); }
    }

    private async Task SendStatus(WebSocket ws)
    {
        // Uses _lastTX (cached) — no serial query
        var status = JsonSerializer.Serialize(new
        {
            type      = "radio_status",
            frequency = _radio.LastFrequency,
            mode      = _radio.LastMode,
            power     = _lastPower,
            tx        = _lastTX
        });
        var buf = Encoding.UTF8.GetBytes(status);
        await ws.SendAsync(buf, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private async Task BroadcastToAll()
    {
        // Uses _lastTX (cached) — no serial query per client
        var status = JsonSerializer.Serialize(new
        {
            type      = "radio_status",
            frequency = _lastFreq,
            mode      = _lastMode,
            power     = _lastPower,
            tx        = _lastTX
        });
        var buf = Encoding.UTF8.GetBytes(status);

        WebSocket[] clients;
        lock (_clientLock) clients = _clients.ToArray();

        foreach (var ws in clients)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.SendAsync(buf, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch { lock (_clientLock) _clients.Remove(ws); }
        }
    }

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
        lock (_clientLock)
        {
            foreach (var ws in _clients)
                try { ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
            _clients.Clear();
        }
    }
}
