using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NAudio.Wave;
using HamDeck.Models;

namespace HamDeck.Services;

/// <summary>
/// Audio recorder with ring buffer for lookback capture and continuous recording.
/// Uses NAudio for Windows audio capture. Also feeds AudioStreamer if attached.
/// </summary>
public class AudioRecorder : IDisposable
{
    private readonly RadioController _radio;
    private readonly Config _config;

    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private MemoryStream? _ringBuffer;
    private WaveFileWriter? _ringWriter;
    private readonly object _lock = new();
    private string? _currentFile;
    private DateTime _recordStart;

    public bool IsRecording { get; private set; }
    public bool IsBuffering { get; private set; }

    /// <summary>
    /// Set this to feed audio data to the WebSocket streamer.
    /// AudioStreamer.FeedAudio() is called from OnDataAvailable.
    /// </summary>
    public AudioStreamer? Streamer { get; set; }

    public AudioRecorder(RadioController radio, Config config)
    {
        _radio = radio;
        _config = config;
    }

    /// <summary>Start the lookback ring buffer (always-on background capture)</summary>
    public void StartBuffer()
    {
        if (IsBuffering) return;
        try
        {
            _waveIn = new WaveInEvent
            {
                DeviceNumber = _config.AudioDevice,
                WaveFormat = new WaveFormat(_config.RecordSampleRate, 16, 1),
                BufferMilliseconds = 100
            };

            _ringBuffer = new MemoryStream();
            _ringWriter = new WaveFileWriter(new IgnoreDisposeStream(_ringBuffer),
                _waveIn.WaveFormat);

            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.StartRecording();
            IsBuffering = true;
            Logger.Info("RECORDER", "Ring buffer started ({0} Hz)", _config.RecordSampleRate);
        }
        catch (Exception ex)
        {
            Logger.Error("RECORDER", "Failed to start buffer: {0}", ex.Message);
        }
    }

    public void StopBuffer()
    {
        IsBuffering = false;
        try
        {
            _waveIn?.StopRecording();
            _waveIn?.Dispose();
            _ringWriter?.Dispose();
            _ringBuffer?.Dispose();
        }
        catch { }
        _waveIn = null;
        _ringWriter = null;
        _ringBuffer = null;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_lock)
        {
            // Write to ring buffer
            _ringWriter?.Write(e.Buffer, 0, e.BytesRecorded);

            // Trim ring buffer if too large
            if (_ringBuffer != null)
            {
                var maxBytes = _config.RecordSampleRate * 2 * _config.RecordBufferSeconds;
                if (_ringBuffer.Length > maxBytes * 2)
                {
                    var data = _ringBuffer.ToArray();
                    var keep = data[^maxBytes..];
                    _ringBuffer.SetLength(0);
                    _ringBuffer.Write(keep);
                }
            }

            // Write to active recording file
            _writer?.Write(e.Buffer, 0, e.BytesRecorded);
        }

        // Feed audio to WebSocket streamer (outside lock — FeedAudio just enqueues)
        Streamer?.FeedAudio(e.Buffer, e.BytesRecorded);
    }

    /// <summary>Start recording to a file</summary>
    public void Start()
    {
        if (IsRecording) return;

        Directory.CreateDirectory(_config.RecordPath);
        var freq = _radio.Connected ? _radio.GetFreq() : 0;
        var band = freq > 0 ? Helpers.BandHelper.GetBand(freq) : "unknown";
        var filename = $"HamDeck_{DateTime.Now:yyyyMMdd_HHmmss}_{band}.wav";
        _currentFile = Path.Combine(_config.RecordPath, filename);

        lock (_lock)
        {
            var format = _waveIn?.WaveFormat ?? new WaveFormat(_config.RecordSampleRate, 16, 1);
            _writer = new WaveFileWriter(_currentFile, format);
            _recordStart = DateTime.UtcNow;
            IsRecording = true;
        }

        Logger.Info("RECORDER", "Recording started: {0}", filename);

        // Start buffer if not already running
        if (!IsBuffering) StartBuffer();
    }

    /// <summary>Stop recording and return the filename</summary>
    public string? Stop()
    {
        if (!IsRecording) return null;

        lock (_lock)
        {
            IsRecording = false;
            _writer?.Dispose();
            _writer = null;
        }

        Logger.Info("RECORDER", "Recording stopped: {0}", Path.GetFileName(_currentFile) ?? "unknown");
        return _currentFile;
    }

    /// <summary>Stop recording and save to a specific path with prefix (for PTT auto-record)</summary>
    public string? StopWithPath(string savePath, string prefix)
    {
        if (!IsRecording) return null;

        string? oldFile;
        lock (_lock)
        {
            IsRecording = false;
            _writer?.Dispose();
            _writer = null;
            oldFile = _currentFile;
        }

        if (oldFile == null || !File.Exists(oldFile)) return null;

        // Move/rename to the QSO path with organized folders
        try
        {
            Directory.CreateDirectory(savePath);
            var freq = _radio.Connected ? _radio.GetFreq() : 0;
            var freqStr = freq > 0 ? $"{freq / 1000.0:F1}kHz" : "unknown";

            // Use UTC for QSO recordings (matching Go behavior)
            string timestamp;
            if (prefix == "qso")
                timestamp = DateTime.UtcNow.ToString("HHmmss") + "Z";
            else
                timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            var newFilename = $"{prefix}_{timestamp}_{freqStr}.wav";
            var newPath = Path.Combine(savePath, newFilename);

            File.Move(oldFile, newPath, true);
            Logger.Info("RECORDER", "Saved: {0}", newPath);
            return newPath;
        }
        catch (Exception ex)
        {
            Logger.Error("RECORDER", "Failed to move recording: {0}", ex.Message);
            return oldFile;
        }
    }

    /// <summary>Save the ring buffer contents as a replay file</summary>
    public string? SaveReplay()
    {
        if (_ringBuffer == null || _ringBuffer.Length == 0) return null;

        Directory.CreateDirectory(_config.RecordPath);
        var filename = $"HamDeck_Replay_{DateTime.Now:yyyyMMdd_HHmmss}.wav";
        var filepath = Path.Combine(_config.RecordPath, filename);

        lock (_lock)
        {
            var data = _ringBuffer.ToArray();
            var format = _waveIn?.WaveFormat ?? new WaveFormat(_config.RecordSampleRate, 16, 1);
            using var writer = new WaveFileWriter(filepath, format);
            writer.Write(data, 0, data.Length);
        }

        Logger.Info("RECORDER", "Replay saved: {0}", filename);
        return filepath;
    }

    public Dictionary<string, object> GetStatus()
    {
        return new()
        {
            ["recording"] = IsRecording,
            ["buffering"] = IsBuffering,
            ["filename"] = _currentFile ?? "",
            ["duration"] = IsRecording ? (DateTime.UtcNow - _recordStart).TotalSeconds : 0,
            ["buffer_size"] = _ringBuffer?.Length ?? 0
        };
    }

    public void Cleanup()
    {
        Stop();
        StopBuffer();
    }

    public void Dispose()
    {
        Cleanup();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Stream wrapper that ignores Dispose - needed for ring buffer MemoryStream</summary>
internal class IgnoreDisposeStream : Stream
{
    private readonly Stream _inner;
    public IgnoreDisposeStream(Stream inner) => _inner = inner;
    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buf, int off, int cnt) => _inner.Read(buf, off, cnt);
    public override long Seek(long off, SeekOrigin org) => _inner.Seek(off, org);
    public override void SetLength(long val) => _inner.SetLength(val);
    public override void Write(byte[] buf, int off, int cnt) => _inner.Write(buf, off, cnt);
    protected override void Dispose(bool disposing) { /* intentionally empty */ }
}
