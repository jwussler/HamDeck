using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HamDeck.Models;

namespace HamDeck.Services;

/// <summary>
/// DX Cluster client - polls the WA0O JSON spot API at a configurable interval.
/// Provides spot data for display and optional tune-to-spot functionality.
/// </summary>
public class DxClusterClient : IDisposable
{
    private readonly RadioController _radio;
    private readonly Config _config;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private CancellationTokenSource? _cts;
    private readonly object _lock = new();

    public bool IsConnected { get; private set; }
    public List<DXSpot> Spots { get; private set; } = new();
    public string LastError { get; private set; } = "";
    public event Action<List<DXSpot>>? OnSpotsUpdated;
    public event Action<string>? OnStatusChanged;

    public DxClusterClient(RadioController radio, Config config)
    {
        _radio = radio;
        _config = config;
    }

    public void Connect()
    {
        if (!_config.ClusterEnabled || string.IsNullOrEmpty(_config.ClusterAPIURL))
        {
            Logger.Info("CLUSTER", "Disabled or no API URL configured");
            return;
        }

        lock (_lock)
        {
            if (IsConnected) return;
            IsConnected = true;
        }

        _cts = new CancellationTokenSource();
        Task.Run(() => PollLoop(_cts.Token));
        Logger.Info("CLUSTER", "Started polling {0} every {1}s", _config.ClusterAPIURL, _config.ClusterPollInterval);
        OnStatusChanged?.Invoke("Connected");
    }

    public void Disconnect()
    {
        _cts?.Cancel();
        lock (_lock) { IsConnected = false; }
        Logger.Info("CLUSTER", "Disconnected");
        OnStatusChanged?.Invoke("Disconnected");
    }

    /// <summary>Force an immediate poll (called from Refresh button)</summary>
    public async Task<string> FetchNow()
    {
        try
        {
            Logger.Info("CLUSTER", "Manual refresh from {0}", _config.ClusterAPIURL);
            var json = await _http.GetStringAsync(_config.ClusterAPIURL);

            // Log first 200 chars of response for diagnostics
            var preview = json.Length > 200 ? json.Substring(0, 200) + "..." : json;
            Logger.Info("CLUSTER", "Response ({0} chars): {1}", json.Length, preview);

            var spots = JsonSerializer.Deserialize<List<DXSpot>>(json);

            if (spots == null)
            {
                LastError = "Deserialized to null";
                Logger.Warn("CLUSTER", "Deserialized to null");
                return "Error: JSON deserialized to null";
            }

            Logger.Info("CLUSTER", "Parsed {0} spots", spots.Count);

            // Parse time and mode
            foreach (var spot in spots)
            {
                if (DateTime.TryParse(spot.When, out var dt))
                    spot.Time = dt.ToUniversalTime();
                spot.Mode = Helpers.BandHelper.GetModeForFrequency(spot.FreqHz);
            }

            Spots = spots;
            LastError = "";
            OnSpotsUpdated?.Invoke(spots);
            return string.Format("OK: {0} spots loaded", spots.Count);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Logger.Error("CLUSTER", "Fetch error: {0}", ex.Message);
            return string.Format("Error: {0}", ex.Message);
        }
    }

    private async Task PollLoop(CancellationToken ct)
    {
        int pollNum = 0;
        while (!ct.IsCancellationRequested)
        {
            pollNum++;
            try
            {
                var json = await _http.GetStringAsync(_config.ClusterAPIURL, ct);
                var spots = JsonSerializer.Deserialize<List<DXSpot>>(json);

                if (spots != null && spots.Count > 0)
                {
                    // Parse time from "When" field and infer mode
                    foreach (var spot in spots)
                    {
                        if (DateTime.TryParse(spot.When, out var dt))
                            spot.Time = dt.ToUniversalTime();
                        spot.Mode = Helpers.BandHelper.GetModeForFrequency(spot.FreqHz);
                    }

                    Spots = spots;
                    LastError = "";
                    OnSpotsUpdated?.Invoke(spots);

                    // Log first 5 polls at Info level, then drop to Debug
                    if (pollNum <= 5)
                        Logger.Info("CLUSTER", "Poll #{0}: Got {1} spots", pollNum, spots.Count);
                    else
                        Logger.Debug("CLUSTER", "Poll #{0}: Got {1} spots", pollNum, spots.Count);
                }
                else
                {
                    Logger.Info("CLUSTER", "Poll #{0}: Empty or null response", pollNum);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                LastError = ex.Message;
                // Always log poll errors at Info so user can see them
                Logger.Info("CLUSTER", "Poll #{0} error: {1}", pollNum, ex.Message);
            }

            try { await Task.Delay(_config.ClusterPollInterval * 1000, ct); }
            catch (OperationCanceledException) { break; }
        }

        lock (_lock) { IsConnected = false; }
    }

    /// <summary>Tune radio to a DX spot frequency</summary>
    public async void TuneToSpot(DXSpot spot)
    {
        if (!_radio.Connected) return;

        // Map cluster mode names to FTDX-101MP CAT mode names
        var radioMode = MapToRadioMode(spot.Mode, spot.FreqHz);

        // Set freq FIRST (may trigger band change), wait for radio to settle, then mode
        _radio.SetFreq(spot.FreqHz);
        await Task.Delay(100);
        _radio.SetMode(radioMode);
        Logger.Info("CLUSTER", "Tuned to {0} on {1} | mode: {2} -> {3}",
            spot.Spotted, spot.DisplayFreq, spot.Mode, radioMode);
    }

    /// <summary>Map DX cluster mode names to Yaesu FTDX-101MP CAT mode codes</summary>
    private static string MapToRadioMode(string clusterMode, long freqHz)
    {
        if (string.IsNullOrEmpty(clusterMode)) return freqHz < 10_000_000 ? "LSB" : "USB";

        switch (clusterMode.ToUpperInvariant())
        {
            // Digital modes → DATA-U (standard for WSJT-X, JS8Call, etc.)
            case "FT8":
            case "FT4":
            case "JT65":
            case "JT9":
            case "JS8":
            case "PSK":
            case "PSK31":
            case "SSTV":
            case "MFSK":
            case "OLIVIA":
                return "DATA-U";

            // RTTY
            case "RTTY":
                return "RTTY-U";

            // CW
            case "CW":
                return "CW-U";

            // Phone modes
            case "SSB":
                return freqHz < 10_000_000 ? "LSB" : "USB";
            case "LSB":
                return "LSB";
            case "USB":
                return "USB";

            // FM/AM
            case "FM":
                return "FM";
            case "AM":
                return "AM";
            case "C4FM":
                return "C4FM";
            case "DMR":
            case "DSTAR":
                return "FM"; // closest match

            default:
                return freqHz < 10_000_000 ? "LSB" : "USB";
        }
    }

    public void Dispose()
    {
        Disconnect();
        _http.Dispose();
        GC.SuppressFinalize(this);
    }
}
