# AudioStreamer Integration Guide

Add real-time WebSocket audio streaming with a built-in web player to HamDeck.

## Files

### 1. NEW: `Services/AudioStreamer.cs`
Copy the provided `AudioStreamer.cs` into your `Services/` directory. No changes needed.

---

### 2. MODIFY: `Models/Config.cs`

Add these properties alongside your other audio settings:

```csharp
// Audio Stream settings
[JsonPropertyName("audio_stream_enabled")] public bool AudioStreamEnabled { get; set; } = false;
[JsonPropertyName("audio_stream_port")] public int AudioStreamPort { get; set; } = 5002;
[JsonPropertyName("audio_stream_sample_rate")] public int AudioStreamSampleRate { get; set; } = 22050;
```

In `ApplyDefaults()`, add:

```csharp
if (AudioStreamPort == 0) AudioStreamPort = 5002;
if (AudioStreamSampleRate == 0) AudioStreamSampleRate = 22050;
```

In `Validate()`, optionally add:

```csharp
if (AudioStreamPort < 1 || AudioStreamPort > 65535) errors.Add("Audio stream port must be 1-65535");
```

---

### 3. MODIFY: `Views/MainWindow.xaml.cs`

Add the field alongside your other services:

```csharp
private readonly AudioStreamer _streamer;
```

In the constructor, after creating `_recorder` and `_radio`, add:

```csharp
_streamer = new AudioStreamer(_radio, _config);
```

In your startup sequence (where you call `_api.Start()`, etc.), add:

```csharp
_streamer.Start();
```

In `Cleanup()`, add before `_radio.Disconnect()`:

```csharp
_streamer.Dispose();
```

---

### 4. OPTIONAL: Add to Settings dialog

If you want a UI toggle, add a checkbox for `AudioStreamEnabled` and a text field
for `AudioStreamPort` in your `SettingsDialog`. The streamer only starts when
`AudioStreamEnabled` is true in config.

---

### 5. OPTIONAL: Add API status endpoint

In `ApiServer.cs`, add a route to check stream status:

```csharp
if (path == "/api/stream/status")
{
    return new
    {
        status = "ok",
        streaming = /* reference to AudioStreamer.IsStreaming */,
        clients = /* reference to AudioStreamer.ClientCount */,
        port = _config.AudioStreamPort,
        url = $"http://localhost:{_config.AudioStreamPort}/"
    };
}
```

This requires passing the AudioStreamer reference to ApiServer, or exposing
the status through a shared object.

---

## How It Works

```
┌─────────────────────────────────────────────────┐
│  HamDeck (Windows)                              │
│                                                 │
│  NAudio WaveInEvent ──► OnAudioData()           │
│    (same audio device)     │                    │
│                            ▼                    │
│              WebSocket broadcast (binary PCM)   │
│                     │                           │
│  HttpListener :5002 │                           │
│    GET /    → HTML player page                  │
│    GET /ws  → WebSocket upgrade ──► audio data  │
│    GET /status → JSON radio info                │
│                                                 │
│  Also sends JSON text frames:                   │
│    { type: "radio_status", frequency, mode, … } │
└─────────────────────────────────────────────────┘
          │
          ▼ (port forward / LAN)
┌─────────────────────────────────────────────────┐
│  Browser (any device)                           │
│                                                 │
│  WebSocket connects to ws://host:5002/ws        │
│    ├─ binary frames → Int16 PCM → Float32       │
│    │    → AudioContext.createBuffer()            │
│    │    → scheduled playback (gapless)           │
│    └─ text frames → frequency/mode display      │
│                                                 │
│  Built-in player shows:                         │
│    • Frequency (MHz) with TX indicator          │
│    • Band + Mode badges                         │
│    • Play/Stop button                           │
│    • Audio level meter                          │
│    • Volume slider                              │
│    • Connection status + listener count         │
└─────────────────────────────────────────────────┘
```

## Bandwidth

| Sample Rate | Bitrate (16-bit mono) | Notes                     |
|-------------|----------------------|---------------------------|
| 8000 Hz     | ~16 KB/s             | Phone quality, minimal BW |
| 22050 Hz    | ~44 KB/s             | Good for ham audio        |
| 44100 Hz    | ~88 KB/s             | CD quality, overkill      |

Default is 22050 Hz which matches your existing AudioRecorder sample rate.

## Public Access

To expose the stream publicly:
1. Port forward `5002` on your router to your HamDeck PC
2. Or use a reverse proxy (nginx/Caddy) pointing to `localhost:5002`
3. The player auto-detects the host from the URL, so it works from any address

## Enabling

Set in `config.json`:
```json
{
  "audio_stream_enabled": true,
  "audio_stream_port": 5002,
  "audio_stream_sample_rate": 22050
}
```

Then visit `http://your-ip:5002/` in any browser.

## Notes

- The AudioStreamer creates its OWN WaveInEvent on the same audio device as
  AudioRecorder — they run independently and don't interfere with each other.
- NAudio supports multiple readers on the same device on Windows.
- No additional NuGet packages required — uses only NAudio (already referenced)
  and the built-in System.Net.WebSockets.
- The HTML player is fully embedded in the C# string — no external files to ship.
- Web Audio API handles sample rate conversion automatically (the AudioContext
  runs at 48kHz and resamples the incoming 22050 Hz data).
