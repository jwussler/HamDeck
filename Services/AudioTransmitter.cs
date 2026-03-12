using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using HamDeck.Models;
using NAudio.Wave;

namespace HamDeck.Services;

/// <summary>
/// Receives PCM audio from a browser WebSocket and plays it to the
/// FTDX-101MP's USB Audio Device for remote transmit.
/// Browser captures mic at 48kHz mono 16-bit PCM → WebSocket → this → NAudio WaveOut.
/// </summary>
public class AudioTransmitter : IDisposable
{
    private readonly Config _config;
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _buffer;
    private VolumeWaveProvider16? _volumeProvider;
    private WebSocket? _activeClient;
    private readonly object _lock = new();
    private volatile bool _active;
    private bool _prebuffering;
    private int _prebufferBytes;

    // 48kHz 16-bit mono — matches browser getUserMedia default
    private const int SampleRate = 48000;
    private const int BitsPerSample = 16;
    private const int Channels = 1;
    private const int IdleTimeoutSeconds = 180; // 3 minutes
    private const int PrebufferMsMin = 200;  // Minimum prebuffer
    private const int PrebufferMsMax = 2000; // Maximum prebuffer
    private const float DefaultGain = 3.0f;  // 3x volume boost for browser mic
    private int _adaptivePrebufferMs = 500;  // Current adaptive target

    public bool IsActive => _active;
    public bool HasClient => _activeClient?.State == WebSocketState.Open;

    public AudioTransmitter(Config config)
    {
        _config = config;
    }

    /// <summary>
    /// Find the NAudio device index for the USB Audio Device (FTDX-101MP codec).
    /// Returns -1 if not found.
    /// </summary>
    public static int FindTxDevice(string deviceName = "USB AUDIO")
    {
        for (int i = 0; i < WaveOut.DeviceCount; i++)
        {
            var caps = WaveOut.GetCapabilities(i);
            if (caps.ProductName.Contains(deviceName, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Info("TXAUDIO", "Found TX device: [{0}] {1}", i, caps.ProductName);
                return i;
            }
        }
        Logger.Warn("TXAUDIO", "TX audio device containing '{0}' not found", deviceName);
        return -1;
    }

    /// <summary>
    /// List all available playback devices (for config/debug).
    /// </summary>
    public static List<(int Index, string Name)> ListDevices()
    {
        var devices = new List<(int, string)>();
        for (int i = 0; i < WaveOut.DeviceCount; i++)
        {
            var caps = WaveOut.GetCapabilities(i);
            devices.Add((i, caps.ProductName));
        }
        return devices;
    }

    /// <summary>
    /// Start the audio output to the TX device.
    /// </summary>
    public bool Start()
    {
        lock (_lock)
        {
            if (_active) return true;

            var deviceIndex = _config.TxAudioDevice >= 0
                ? _config.TxAudioDevice
                : FindTxDevice();

            if (deviceIndex < 0)
            {
                Logger.Error("TXAUDIO", "No TX audio device available");
                return false;
            }

            try
            {
                var format = new WaveFormat(SampleRate, BitsPerSample, Channels);
                _buffer = new BufferedWaveProvider(format)
                {
                    BufferDuration = TimeSpan.FromSeconds(10),
                    DiscardOnBufferOverflow = true
                };

                // Volume boost — browser mic levels are typically low
                _volumeProvider = new VolumeWaveProvider16(_buffer)
                {
                    Volume = DefaultGain
                };

                _waveOut = new WaveOutEvent
                {
                    DeviceNumber = deviceIndex,
                    DesiredLatency = 150
                };
                _waveOut.Init(_volumeProvider);

                // Enable prebuffering — collect audio before starting playback
                _prebufferBytes = SampleRate * (BitsPerSample / 8) * Channels * _adaptivePrebufferMs / 1000;
                _prebuffering = true;

                _active = true;
                Logger.Info("TXAUDIO", "Started on device [{0}], {1}Hz {2}ch {3}bit, gain={4:F1}x, prebuffer={5}ms",
                    deviceIndex, SampleRate, Channels, BitsPerSample, DefaultGain, _adaptivePrebufferMs);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("TXAUDIO", "Failed to start: {0}", ex.Message);
                Stop();
                return false;
            }
        }
    }

    /// <summary>
    /// Stop the audio output.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            _active = false;
            _prebuffering = true;
            _adaptivePrebufferMs = 500; // Reset to default for next connection
            try { _waveOut?.Stop(); } catch { }
            try { _waveOut?.Dispose(); } catch { }
            _waveOut = null;
            _volumeProvider = null;
            _buffer = null;
            Logger.Info("TXAUDIO", "Stopped");
        }
    }

    /// <summary>
    /// Handle a WebSocket connection for TX audio.
    /// Only one client can stream TX audio at a time.
    /// </summary>
    public async Task HandleWebSocketClient(HttpListenerContext httpCtx, CancellationToken ct)
    {
        // Only allow one TX audio client
        if (_activeClient?.State == WebSocketState.Open)
        {
            Logger.Warn("TXAUDIO", "Rejected second client — already streaming");
            try
            {
                var rejectCtx = await httpCtx.AcceptWebSocketAsync(null);
                await rejectCtx.WebSocket.CloseAsync(
                    WebSocketCloseStatus.PolicyViolation,
                    "Another client is already streaming TX audio",
                    CancellationToken.None);
            }
            catch { }
            return;
        }

        WebSocketContext wsCtx;
        try
        {
            wsCtx = await httpCtx.AcceptWebSocketAsync(null);
        }
        catch (Exception ex)
        {
            Logger.Error("TXAUDIO", "WebSocket accept failed: {0}", ex.Message);
            return;
        }

        var ws = wsCtx.WebSocket;
        _activeClient = ws;

        // Start audio output if not already running
        if (!_active && !Start())
        {
            await ws.CloseAsync(WebSocketCloseStatus.InternalServerError,
                "Failed to open TX audio device", ct);
            return;
        }

        Logger.Info("TXAUDIO", "Client connected — streaming to radio");

        try
        {
            var recvBuf = new byte[8192];
            var lastActivity = DateTime.UtcNow;

            // Jitter tracking for adaptive prebuffer
            var lastPacketTime = DateTime.UtcNow;
            var gapSamples = new List<double>();
            const int JitterWindow = 30;
            int underrunCount = 0;

            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                // Check idle timeout
                if ((DateTime.UtcNow - lastActivity).TotalSeconds > IdleTimeoutSeconds)
                {
                    Logger.Info("TXAUDIO", "Client idle for {0}s — disconnecting", IdleTimeoutSeconds);
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure,
                        "Idle timeout (3 minutes)", CancellationToken.None);
                    break;
                }

                // Use a short timeout so we can check idle periodically
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(5000); // 5 second receive timeout

                WebSocketReceiveResult result;
                try
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(recvBuf), timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // 5s receive timeout — loop back to check idle
                    continue;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType == WebSocketMessageType.Binary && result.Count > 0 && _buffer != null)
                {
                    _buffer.AddSamples(recvBuf, 0, result.Count);
                    lastActivity = DateTime.UtcNow;

                    // Track inter-packet jitter
                    var now = DateTime.UtcNow;
                    var gapMs = (now - lastPacketTime).TotalMilliseconds;
                    lastPacketTime = now;
                    gapSamples.Add(gapMs);
                    if (gapSamples.Count > JitterWindow)
                        gapSamples.RemoveAt(0);

                    // Detect buffer underrun (buffer ran dry)
                    if (!_prebuffering && _buffer.BufferedBytes == 0)
                    {
                        underrunCount++;
                        // Increase prebuffer on underrun
                        _adaptivePrebufferMs = Math.Min(PrebufferMsMax, _adaptivePrebufferMs + 100);
                        Logger.Info("TXAUDIO", "Buffer underrun #{0} — increasing prebuffer to {1}ms",
                            underrunCount, _adaptivePrebufferMs);
                        // Re-enter prebuffer mode
                        _waveOut?.Pause();
                        _prebufferBytes = SampleRate * (BitsPerSample / 8) * Channels * _adaptivePrebufferMs / 1000;
                        _prebuffering = true;
                    }

                    // Adapt prebuffer based on jitter when stable
                    if (gapSamples.Count >= 10 && !_prebuffering)
                    {
                        var avg = gapSamples.Average();
                        var variance = gapSamples.Sum(g => (g - avg) * (g - avg)) / gapSamples.Count;
                        var jitterMs = Math.Sqrt(variance);

                        // Target = 3x jitter, clamped
                        var target = (int)Math.Max(PrebufferMsMin, Math.Min(PrebufferMsMax, jitterMs * 3));
                        // Smooth: slowly decrease, quickly increase
                        if (target > _adaptivePrebufferMs)
                            _adaptivePrebufferMs = target;
                        else
                            _adaptivePrebufferMs = (int)(_adaptivePrebufferMs * 0.95 + target * 0.05);
                    }

                    // Start playback after prebuffer is filled (jitter buffer)
                    if (_prebuffering && _buffer.BufferedBytes >= _prebufferBytes)
                    {
                        _waveOut?.Play();
                        _prebuffering = false;
                        Logger.Info("TXAUDIO", "Prebuffer filled ({0} bytes, {1}ms) — playback started",
                            _buffer.BufferedBytes, _adaptivePrebufferMs);
                    }
                }
            }
        }
        catch (WebSocketException) { /* client disconnected */ }
        catch (OperationCanceledException) { /* server shutting down */ }
        catch (Exception ex)
        {
            Logger.Error("TXAUDIO", "Client error: {0}", ex.Message);
        }
        finally
        {
            _activeClient = null;
            Logger.Info("TXAUDIO", "Client disconnected");

            // Stop playback when client disconnects
            Stop();

            if (ws.State == WebSocketState.Open)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
                catch { }
            }
        }
    }

    public void Dispose()
    {
        Stop();
        try
        {
            if (_activeClient?.State == WebSocketState.Open)
                _activeClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).Wait(1000);
        }
        catch { }
    }
}
