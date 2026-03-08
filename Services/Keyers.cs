using System;
using System.IO;
using System.Threading;
using NAudio.Wave;
using HamDeck.Models;

namespace HamDeck.Services;

/// <summary>CW keyer - sends CW text and manages CW memories via radio CAT commands</summary>
public class CwKeyer
{
    private readonly RadioController _radio;
    private readonly string[] _memories = new string[5];

    public CwKeyer(RadioController radio) => _radio = radio;

    public void SendText(string text) => _radio.SendCWText(text);
    public void PlayMemory(int slot) => _radio.PlayCWMemory(slot);
    public void StopPlayback() => _radio.StopCWMemory();

    /// <summary>
    /// Returns true if the radio's CW memory playback is active.
    /// Implemented via the KY; CAT query — the FTDX-101 responds KY1; during playback,
    /// KY0; when idle. Falls back to false if the radio is not connected.
    /// </summary>
    public bool IsPlaying() => _radio.GetCWMemoryStatus();

    public void SetMemoryText(int slot, string text)
    {
        if (slot >= 0 && slot < _memories.Length)
            _memories[slot] = text;
    }

    public string GetMemoryText(int slot) =>
        slot >= 0 && slot < _memories.Length ? _memories[slot] ?? "" : "";
}

/// <summary>Voice keyer - plays WAV files through the radio mic input with PTT control</summary>
public class VoiceKeyer : IDisposable
{
    private readonly RadioController _radio;
    private readonly Config _config;
    private readonly string?[] _slots = new string?[5];
    private WaveOutEvent? _player;
    private bool _isPlaying;

    public bool IsPlaying => _isPlaying;
    public event Action<int>? OnPlayStart;
    public event Action? OnPlayEnd;

    public VoiceKeyer(RadioController radio, Config config)
    {
        _radio = radio;
        _config = config;
        LoadSlots();
    }

    private void LoadSlots()
    {
        var dir = Path.Combine(Config.ConfigDir, "voicekeyer");
        if (!Directory.Exists(dir)) return;
        for (int i = 0; i < _slots.Length; i++)
        {
            var path = Path.Combine(dir, $"memory_{i + 1}.wav");
            if (File.Exists(path)) _slots[i] = path;
        }
    }

    public void SetSlot(int slot, string path)
    {
        if (slot >= 0 && slot < _slots.Length)
        {
            _slots[slot] = path;
            // Copy file to standard location
            var dir = Path.Combine(Config.ConfigDir, "voicekeyer");
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, $"memory_{slot + 1}.wav");
            if (File.Exists(path)) File.Copy(path, dest, true);
        }
    }

    public string? GetSlotPath(int slot) =>
        slot >= 0 && slot < _slots.Length ? _slots[slot] : null;

    public void Play(int slot)
    {
        if (slot < 0 || slot >= _slots.Length || string.IsNullOrEmpty(_slots[slot])) return;
        if (_isPlaying) StopPlayback();

        try
        {
            var reader = new AudioFileReader(_slots[slot]!);
            _player = new WaveOutEvent { DeviceNumber = _config.MicDevice };
            _player.Init(reader);
            _player.PlaybackStopped += (_, _) =>
            {
                _radio.SetPTT(false);
                _isPlaying = false;
                OnPlayEnd?.Invoke();
                reader.Dispose();
            };

            _radio.SetPTT(true);
            Thread.Sleep(200); // Delay for PTT to activate
            _player.Play();
            _isPlaying = true;
            OnPlayStart?.Invoke(slot);
            Logger.Info("VOICE", "Playing memory {0}", slot + 1);
        }
        catch (Exception ex)
        {
            Logger.Error("VOICE", "Play error: {0}", ex.Message);
            _radio.SetPTT(false);
            _isPlaying = false;
        }
    }

    public void StopPlayback()
    {
        _player?.Stop();
        _radio.SetPTT(false);
        _isPlaying = false;
    }

    public void Cleanup() => StopPlayback();
    public void Dispose() { Cleanup(); GC.SuppressFinalize(this); }
}
