using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HamDeck.Models;

namespace HamDeck.Services;

/// <summary>
/// WebSocket audio broadcast service.
/// 
/// Single send loop handles BOTH audio data (binary) and status updates (text).
/// This prevents the "already one outstanding SendAsync" error that occurs when
/// two separate loops call SendAsync on the same WebSocket simultaneously.
/// </summary>
public class AudioStreamer : IDisposable
{
    private readonly Config _config;
    private readonly ConcurrentDictionary<string, WebSocket> _clients = new();
    private readonly ConcurrentQueue<QueueItem> _sendQueue = new();
    private readonly SemaphoreSlim _dataReady = new(0);
    private CancellationTokenSource? _cts;
    private int _clientIdCounter;

    /// <summary>Queue item: either binary audio or text status JSON.</summary>
    private readonly record struct QueueItem(byte[] Data, WebSocketMessageType Type);

    public bool IsStreaming { get; private set; }
    public int ClientCount => _clients.Count;

    // Cached radio status
    private long _cachedFreq;
    private string _cachedMode = "";
    private string _cachedBand = "";
    private int _cachedPower;
    private bool _cachedTx;
    private bool _cachedConnected;

    public void UpdateStatus(long freq, string mode, string band, int power, bool tx, bool connected)
    {
        _cachedFreq = freq;
        _cachedMode = mode;
        _cachedBand = band;
        _cachedPower = power;
        _cachedTx = tx;
        _cachedConnected = connected;
    }

    public AudioStreamer(Config config)
    {
        _config = config;
    }

    public void Start()
    {
        if (!_config.AudioStreamEnabled)
        {
            Logger.Info("STREAM", "Audio stream disabled in settings");
            return;
        }

        _cts = new CancellationTokenSource();
        IsStreaming = true;

        // Single send loop — handles both audio and status
        Task.Factory.StartNew(() => SendLoopAsync(_cts.Token),
            _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        // Status producer — enqueues status JSON into the same queue
        Task.Factory.StartNew(() => StatusProducerAsync(_cts.Token),
            _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        Logger.Info("STREAM", "Audio streamer started ({0} Hz, 16-bit mono)",
            _config.RecordSampleRate);
    }

    /// <summary>
    /// Called by AudioRecorder.OnDataAvailable. Must return instantly.
    /// </summary>
    public void FeedAudio(byte[] buffer, int bytesRecorded)
    {
        if (!IsStreaming || _clients.IsEmpty || bytesRecorded == 0) return;

        var data = new byte[bytesRecorded];
        Buffer.BlockCopy(buffer, 0, data, 0, bytesRecorded);

        // Drop oldest if backing up (~1 second max)
        while (_sendQueue.Count > 10)
            _sendQueue.TryDequeue(out _);

        _sendQueue.Enqueue(new QueueItem(data, WebSocketMessageType.Binary));
        _dataReady.Release();
    }

    /// <summary>
    /// Produces status JSON messages into the same queue as audio data.
    /// Single send loop consumes both — no concurrent SendAsync on the same socket.
    /// </summary>
    private async Task StatusProducerAsync(CancellationToken ct)
    {
        long lastFreq = 0;
        string lastMode = "";

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(250, ct); }
            catch (OperationCanceledException) { break; }

            if (_clients.IsEmpty || !_cachedConnected) continue;

            var freq = _cachedFreq;
            var mode = _cachedMode;

            if (freq == lastFreq && mode == lastMode) continue;
            lastFreq = freq;
            lastMode = mode;

            var status = JsonSerializer.Serialize(new
            {
                type = "radio_status",
                frequency = freq,
                mode,
                band = _cachedBand,
                power = _cachedPower,
                tx = _cachedTx,
                clients = _clients.Count,
                streaming = IsStreaming,
                sample_rate = _config.RecordSampleRate
            });

            _sendQueue.Enqueue(new QueueItem(Encoding.UTF8.GetBytes(status), WebSocketMessageType.Text));
            _dataReady.Release();
        }
    }

    /// <summary>
    /// Single async send loop. Drains the queue and sends to all clients.
    /// Both audio (binary) and status (text) go through here — guarantees
    /// only one SendAsync per client at a time.
    /// </summary>
    private async Task SendLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _dataReady.WaitAsync(500, ct);
            }
            catch (OperationCanceledException) { break; }

            while (_sendQueue.TryDequeue(out var item))
            {
                if (ct.IsCancellationRequested) return;
                if (_clients.IsEmpty) continue;

                List<string>? dead = null;

                foreach (var kvp in _clients)
                {
                    if (kvp.Value.State != WebSocketState.Open)
                    {
                        (dead ??= new()).Add(kvp.Key);
                        continue;
                    }

                    try
                    {
                        using var timeout = new CancellationTokenSource(1000);
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                        await kvp.Value.SendAsync(item.Data, item.Type, true, linked.Token);
                    }
                    catch
                    {
                        (dead ??= new()).Add(kvp.Key);
                    }
                }

                if (dead != null)
                    foreach (var id in dead)
                        _clients.TryRemove(id, out _);
            }
        }
    }

    public async Task HandleWebSocketClient(HttpListenerContext ctx, CancellationToken ct)
    {
        WebSocket? ws = null;
        string? clientId = null;
        try
        {
            var wsCtx = await ctx.AcceptWebSocketAsync(null);
            ws = wsCtx.WebSocket;
            clientId = $"client_{Interlocked.Increment(ref _clientIdCounter)}";
            _clients.TryAdd(clientId, ws);

            var remoteAddr = ctx.Request.RemoteEndPoint?.ToString() ?? "unknown";
            Logger.Info("STREAM", "Client connected: {0} ({1})", clientId, remoteAddr);

            // Send config via the queue so it doesn't race with other sends
            var config = JsonSerializer.Serialize(new
            {
                type = "config",
                sample_rate = _config.RecordSampleRate,
                channels = 1,
                bits_per_sample = 16
            });
            _sendQueue.Enqueue(new QueueItem(Encoding.UTF8.GetBytes(config), WebSocketMessageType.Text));

            // Send immediate radio status so the player shows frequency right away
            if (_cachedConnected && _cachedFreq > 0)
            {
                var status = JsonSerializer.Serialize(new
                {
                    type = "radio_status",
                    frequency = _cachedFreq,
                    mode = _cachedMode,
                    band = _cachedBand,
                    power = _cachedPower,
                    tx = _cachedTx,
                    clients = _clients.Count,
                    streaming = IsStreaming,
                    sample_rate = _config.RecordSampleRate
                });
                _sendQueue.Enqueue(new QueueItem(Encoding.UTF8.GetBytes(status), WebSocketMessageType.Text));
            }
            _dataReady.Release();

            // Keep connection alive
            var buf = new byte[256];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buf, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (Exception ex)
        {
            Logger.Debug("STREAM", "WebSocket error: {0}", ex.Message);
        }
        finally
        {
            if (clientId != null)
            {
                _clients.TryRemove(clientId, out _);
                Logger.Info("STREAM", "Client disconnected: {0}", clientId);
            }
            if (ws?.State == WebSocketState.Open)
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        }
    }

    public string GetPlayerHtml()
    {
        return """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>HamDeck Audio — WA0O</title>
<style>
  @import url('https://fonts.googleapis.com/css2?family=JetBrains+Mono:wght@400;700&family=Orbitron:wght@400;700;900&display=swap');
  *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
  :root {
    --bg: #0a0e17; --card: #111827; --border: #1e2d4a;
    --primary: #00d4ff; --primary-dim: #00d4ff44; --accent: #ff6b35;
    --green: #22c55e; --red: #ef4444; --text: #e2e8f0; --dim: #64748b;
    --glow: 0 0 20px #00d4ff33, 0 0 60px #00d4ff11;
  }
  body { font-family: 'JetBrains Mono', monospace; background: var(--bg); color: var(--text);
    min-height: 100vh; display: flex; flex-direction: column; align-items: center;
    justify-content: center; padding: 2rem;
    background-image: radial-gradient(ellipse at 20% 50%, #00d4ff08 0%, transparent 50%),
      radial-gradient(ellipse at 80% 50%, #ff6b3508 0%, transparent 50%); }
  .container { width: 100%; max-width: 520px; }
  .header { text-align: center; margin-bottom: 2rem; }
  .header h1 { font-family: 'Orbitron', sans-serif; font-size: 1.6rem; font-weight: 900;
    color: var(--primary); letter-spacing: 0.15em; text-shadow: 0 0 30px #00d4ff44; }
  .header .callsign { font-family: 'Orbitron', sans-serif; font-size: 0.85rem;
    color: var(--dim); letter-spacing: 0.3em; margin-top: 0.3rem; }
  .panel { background: var(--card); border: 1px solid var(--border); border-radius: 12px;
    padding: 1.5rem; margin-bottom: 1rem; box-shadow: var(--glow); }
  .freq-display { text-align: center; padding: 1.2rem 0; }
  .freq-value { font-family: 'Orbitron', sans-serif; font-size: 2.8rem; font-weight: 700;
    color: var(--primary); letter-spacing: 0.05em; text-shadow: 0 0 20px #00d4ff55; transition: color 0.3s; }
  .freq-value.tx { color: var(--red); text-shadow: 0 0 20px #ef444455; }
  .freq-unit { font-size: 1rem; color: var(--dim); margin-left: 0.3rem; }
  .radio-info { display: flex; justify-content: center; gap: 1.5rem; margin-top: 0.6rem; }
  .info-badge { font-size: 0.75rem; font-weight: 700; padding: 0.2rem 0.7rem; border-radius: 4px;
    background: var(--primary-dim); color: var(--primary); letter-spacing: 0.1em; }
  .info-badge.mode { background: #ff6b3533; color: var(--accent); }
  .info-badge.tx-badge { background: #ef444433; color: var(--red); animation: pulse 1s infinite; }
  @keyframes pulse { 0%, 100% { opacity: 1; } 50% { opacity: 0.5; } }
  .player { display: flex; flex-direction: column; align-items: center; gap: 1rem; }
  .play-btn { width: 72px; height: 72px; border-radius: 50%; border: 2px solid var(--primary);
    background: transparent; color: var(--primary); cursor: pointer; display: flex;
    align-items: center; justify-content: center; transition: all 0.2s; }
  .play-btn:hover { background: var(--primary-dim); transform: scale(1.05); }
  .play-btn:active { transform: scale(0.95); }
  .play-btn.playing { border-color: var(--green); color: var(--green); box-shadow: 0 0 20px #22c55e33; }
  .play-btn svg { width: 28px; height: 28px; fill: currentColor; }
  .volume-row { display: flex; align-items: center; gap: 0.8rem; width: 100%; max-width: 300px; }
  .volume-row label { font-size: 0.7rem; color: var(--dim); letter-spacing: 0.1em; min-width: 30px; }
  input[type="range"] { -webkit-appearance: none; appearance: none; flex: 1; height: 4px;
    background: var(--border); border-radius: 2px; outline: none; }
  input[type="range"]::-webkit-slider-thumb { -webkit-appearance: none; width: 16px; height: 16px;
    border-radius: 50%; background: var(--primary); cursor: pointer; box-shadow: 0 0 8px #00d4ff44; }
  .status-bar { display: flex; justify-content: space-between; align-items: center;
    font-size: 0.7rem; color: var(--dim); padding: 0.5rem 0; }
  .status-dot { display: inline-block; width: 8px; height: 8px; border-radius: 50%;
    margin-right: 0.4rem; background: var(--red); transition: background 0.3s; }
  .status-dot.connected { background: var(--green); box-shadow: 0 0 6px #22c55e66; }
  .meter-container { width: 100%; max-width: 300px; height: 6px; background: var(--border);
    border-radius: 3px; overflow: hidden; }
  .meter-fill { height: 100%; width: 0%; background: linear-gradient(90deg, var(--green), var(--primary), var(--accent));
    border-radius: 3px; transition: width 0.1s; }
  @media (max-width: 480px) { .freq-value { font-size: 2rem; } .panel { padding: 1rem; } body { padding: 1rem; } }
</style>
</head>
<body>
  <div class="container">
    <div class="header"><h1>HAMDECK AUDIO</h1><div class="callsign">WA0O</div></div>
    <div class="panel">
      <div class="freq-display">
        <span class="freq-value" id="freq">———</span><span class="freq-unit">MHz</span>
        <div class="radio-info">
          <span class="info-badge" id="band">——</span>
          <span class="info-badge mode" id="mode">——</span>
          <span class="info-badge tx-badge" id="txBadge" style="display:none">TX</span>
        </div>
      </div>
    </div>
    <div class="panel">
      <div class="player">
        <button class="play-btn" id="playBtn" onclick="togglePlay()">
          <svg id="playIcon" viewBox="0 0 24 24"><polygon points="6,3 20,12 6,21"/></svg>
          <svg id="stopIcon" viewBox="0 0 24 24" style="display:none"><rect x="5" y="5" width="14" height="14" rx="2"/></svg>
        </button>
        <div class="meter-container"><div class="meter-fill" id="meter"></div></div>
        <div class="volume-row">
          <label>VOL</label>
          <input type="range" id="volume" min="0" max="100" value="75" oninput="setVolume(this.value)">
          <label id="volLabel">75%</label>
        </div>
      </div>
    </div>
    <div class="status-bar">
      <div><span class="status-dot" id="statusDot"></span><span id="statusText">Disconnected</span></div>
      <div id="clientCount"></div>
    </div>
  </div>
<script>
(function() {
  'use strict';
  let ws=null,audioCtx=null,gainNode=null,playing=false,sampleRate=22050,reconnectTimer=null,nextPlayTime=0;
  const freqEl=document.getElementById('freq'),bandEl=document.getElementById('band');
  const modeEl=document.getElementById('mode'),txBadge=document.getElementById('txBadge');
  const playBtn=document.getElementById('playBtn'),playIcon=document.getElementById('playIcon');
  const stopIcon=document.getElementById('stopIcon'),meter=document.getElementById('meter');
  const statusDot=document.getElementById('statusDot'),statusText=document.getElementById('statusText');
  const clientCount=document.getElementById('clientCount'),volLabel=document.getElementById('volLabel');
  function formatFreq(hz){if(!hz||hz===0)return'———';const m=hz/1e6;return m.toFixed(m>=100?3:m>=10?4:5);}
  function connect(){
    if(ws&&ws.readyState<=1)return;
    ws=new WebSocket((location.protocol==='https:'?'wss:':'ws:')+'//'+location.host+'/ws');
    ws.binaryType='arraybuffer';
    ws.onopen=()=>{statusDot.classList.add('connected');statusText.textContent='Connected';if(reconnectTimer){clearTimeout(reconnectTimer);reconnectTimer=null;}};
    ws.onclose=()=>{statusDot.classList.remove('connected');statusText.textContent='Disconnected';scheduleReconnect();};
    ws.onerror=()=>{statusDot.classList.remove('connected');statusText.textContent='Error';};
    ws.onmessage=(e)=>{if(typeof e.data==='string'){const m=JSON.parse(e.data);if(m.type==='config')sampleRate=m.sample_rate||22050;if(m.type==='radio_status'){freqEl.textContent=formatFreq(m.frequency);freqEl.classList.toggle('tx',!!m.tx);bandEl.textContent=m.band||'——';modeEl.textContent=m.mode||'——';txBadge.style.display=m.tx?'inline-block':'none';if(m.clients!==undefined)clientCount.textContent=m.clients+' listener'+(m.clients!==1?'s':'');}}else if(playing)handleAudio(e.data);};
  }
  function scheduleReconnect(){if(reconnectTimer)return;reconnectTimer=setTimeout(()=>{reconnectTimer=null;connect();},3000);}
  function handleAudio(ab){
    if(!audioCtx||audioCtx.state==='closed')return;
    const i16=new Int16Array(ab),f32=new Float32Array(i16.length);
    for(let i=0;i<i16.length;i++)f32[i]=i16[i]/32768.0;
    let s=0;for(let i=0;i<f32.length;i++)s+=f32[i]*f32[i];
    meter.style.width=Math.min(100,Math.max(0,(20*Math.log10(Math.max(Math.sqrt(s/f32.length),1e-6))+50)*2))+'%';
    const buf=audioCtx.createBuffer(1,f32.length,sampleRate);buf.getChannelData(0).set(f32);
    const src=audioCtx.createBufferSource();src.buffer=buf;src.connect(gainNode);
    const now=audioCtx.currentTime;if(nextPlayTime<now)nextPlayTime=now+0.05;
    src.start(nextPlayTime);nextPlayTime+=buf.duration;
  }
  window.togglePlay=function(){playing?stopAudio():startAudio();};
  function startAudio(){
    audioCtx=new(window.AudioContext||window.webkitAudioContext)({sampleRate:48000});
    gainNode=audioCtx.createGain();gainNode.gain.value=document.getElementById('volume').value/100;
    gainNode.connect(audioCtx.destination);playing=true;nextPlayTime=0;
    playBtn.classList.add('playing');playIcon.style.display='none';stopIcon.style.display='block';
    if(!ws||ws.readyState>1)connect();
  }
  function stopAudio(){
    playing=false;if(audioCtx){audioCtx.close().catch(()=>{});audioCtx=null;}
    gainNode=null;playBtn.classList.remove('playing');playIcon.style.display='block';stopIcon.style.display='none';meter.style.width='0%';
  }
  window.setVolume=function(v){volLabel.textContent=v+'%';if(gainNode)gainNode.gain.value=v/100;};
  connect();
})();
</script>
</body>
</html>
""";
    }

    public void Dispose()
    {
        IsStreaming = false;
        _cts?.Cancel();
        try { _dataReady.Release(); } catch { }

        foreach (var kvp in _clients)
        {
            try { kvp.Value.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).Wait(1000); }
            catch { }
        }
        _clients.Clear();
        _dataReady.Dispose();

        Logger.Info("STREAM", "Audio streamer stopped");
    }
}
