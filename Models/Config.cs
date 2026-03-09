using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HamDeck.Models;

/// <summary>
/// Application configuration - persisted to JSON
/// </summary>
public class Config
{
    // Radio settings
    [JsonPropertyName("radio_port")] public string RadioPort { get; set; } = "COM11";
    [JsonPropertyName("radio_baud")] public int RadioBaud { get; set; } = 38400;

    // FlexKnob settings
    [JsonPropertyName("flexknob_port")] public string FlexknobPort { get; set; } = "COM13";
    [JsonPropertyName("flexknob_baud")] public int FlexknobBaud { get; set; } = 9600;
    [JsonPropertyName("flexknob_enabled")] public bool FlexknobEnabled { get; set; } = true;
    [JsonPropertyName("flexknob_default_step")] public int FlexknobDefaultStep { get; set; } = 100;

    // Audio/Recording settings
    [JsonPropertyName("audio_device")] public int AudioDevice { get; set; } = -1;
    [JsonPropertyName("mic_device")] public int MicDevice { get; set; } = -1;
    [JsonPropertyName("record_sample_rate")] public int RecordSampleRate { get; set; } = 22050;
    [JsonPropertyName("record_buffer_seconds")] public int RecordBufferSeconds { get; set; } = 60;
    [JsonPropertyName("record_max_seconds")] public int RecordMaxSeconds { get; set; } = 10800;
    [JsonPropertyName("record_warning_seconds")] public int RecordWarningSeconds { get; set; } = 300;
    [JsonPropertyName("record_path")] public string RecordPath { get; set; } = "";
    [JsonPropertyName("ptt_record_enabled")] public bool PTTRecordEnabled { get; set; } = true;
    [JsonPropertyName("ptt_record_path")] public string PTTRecordPath { get; set; } = "";
    [JsonPropertyName("ptt_record_seconds")] public int PTTRecordSeconds { get; set; } = 60;
    [JsonPropertyName("ptt_qsy_threshold_khz")] public int PTTQSYThresholdKHz { get; set; } = 10;

    // API settings
    [JsonPropertyName("api_port")] public int APIPort { get; set; } = 5001;
    [JsonPropertyName("api_enabled")] public bool APIEnabled { get; set; } = true;

    // TCP CAT Proxy settings (for N1MM and other loggers)
    [JsonPropertyName("cat_proxy_enabled")] public bool CatProxyEnabled { get; set; } = false;
    [JsonPropertyName("cat_proxy_port")] public int CatProxyPort { get; set; } = 4532;

    // Audio Stream settings
    [JsonPropertyName("audio_stream_enabled")] public bool AudioStreamEnabled { get; set; } = false;
    [JsonPropertyName("audio_stream_port")] public int AudioStreamPort { get; set; } = 5003;
    [JsonPropertyName("audio_stream_sample_rate")] public int AudioStreamSampleRate { get; set; } = 22050;

    // TG-XL Tuner settings
    [JsonPropertyName("tgxl_host")] public string TGXLHost { get; set; } = "192.168.40.51";
    [JsonPropertyName("tgxl_port")] public int TGXLPort { get; set; } = 9010;

    // KMTronic RX antenna relay settings
    [JsonPropertyName("kmtronic_host")] public string KmtronicHost { get; set; } = "192.168.40.69";
    [JsonPropertyName("kmtronic_port")] public int KmtronicPort { get; set; } = 12345;
    [JsonPropertyName("kmtronic_enabled")] public bool KmtronicEnabled { get; set; } = true;

    // Wavelog settings
    [JsonPropertyName("wavelog_url")] public string WavelogURL { get; set; } = "";
    [JsonPropertyName("wavelog_api_key")] public string WavelogAPIKey { get; set; } = "";
    [JsonPropertyName("wavelog_station_id")] public int WavelogStationID { get; set; } = 1;
    [JsonPropertyName("wavelog_enabled")] public bool WavelogEnabled { get; set; }

    // Window behavior
    [JsonPropertyName("start_minimized")] public bool StartMinimized { get; set; }
    [JsonPropertyName("minimize_to_tray")] public bool MinimizeToTray { get; set; } = true;

    // DX Cluster settings
    [JsonPropertyName("cluster_host")] public string ClusterHost { get; set; } = "dxc.nc7j.com";
    [JsonPropertyName("cluster_port")] public int ClusterPort { get; set; } = 7373;
    [JsonPropertyName("cluster_callsign")] public string ClusterCallsign { get; set; } = "";
    [JsonPropertyName("cluster_enabled")] public bool ClusterEnabled { get; set; } = true;
    [JsonPropertyName("cluster_api_url")] public string ClusterAPIURL { get; set; } = "https://api.wa0o.com/dxcache/spots";
    [JsonPropertyName("cluster_poll_interval")] public int ClusterPollInterval { get; set; } = 30;

    // Voice Keyer
    [JsonPropertyName("voicekeyer_enabled")] public bool VoiceKeyerEnabled { get; set; }

    // Logging
    [JsonPropertyName("log_level")] public string LogLevel { get; set; } = "info";
    [JsonPropertyName("log_to_file")] public bool LogToFile { get; set; }

    // UI preferences
    [JsonPropertyName("show_cluster_panel")] public bool ShowClusterPanel { get; set; } = true;
    [JsonPropertyName("show_stats_panel")] public bool ShowStatsPanel { get; set; }

    // --- Computed paths ---
    [JsonIgnore]
    public static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hamdeck");

    [JsonIgnore]
    public static string ConfigFile => Path.Combine(ConfigDir, "config.json");

    public static string DefaultRecordPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HamDeck_Recordings");

    public static string DefaultPTTRecordPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HamDeck_QSOs");

    /// <summary>Load config from disk, or create default</summary>
    public static Config Load()
    {
        Directory.CreateDirectory(ConfigDir);
        try
        {
            if (File.Exists(ConfigFile))
            {
                var json = File.ReadAllText(ConfigFile);
                var cfg = JsonSerializer.Deserialize<Config>(json) ?? new Config();
                cfg.ApplyDefaults();
                return cfg;
            }
        }
        catch { /* fall through to defaults */ }

        var config = new Config();
        config.ApplyDefaults();
        config.Save();
        return config;
    }

    /// <summary>Persist config to disk</summary>
    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(ConfigFile, JsonSerializer.Serialize(this, opts));
    }

    /// <summary>Fill empty fields with defaults</summary>
    public void ApplyDefaults()
    {
        if (RadioBaud == 0) RadioBaud = 38400;
        if (FlexknobBaud == 0) FlexknobBaud = 9600;
        if (RecordSampleRate == 0) RecordSampleRate = 22050;
        if (RecordBufferSeconds == 0) RecordBufferSeconds = 60;
        if (RecordMaxSeconds == 0) RecordMaxSeconds = 10800;
        if (RecordWarningSeconds == 0) RecordWarningSeconds = 300;
        if (PTTRecordSeconds == 0) PTTRecordSeconds = 60;
        if (PTTQSYThresholdKHz == 0) PTTQSYThresholdKHz = 10;
        if (APIPort == 0) APIPort = 5001;
        if (CatProxyPort == 0) CatProxyPort = 4532;
        if (AudioStreamPort == 0) AudioStreamPort = 5003;
        if (AudioStreamSampleRate == 0) AudioStreamSampleRate = 22050;
        if (TGXLPort == 0) TGXLPort = 9010;
        if (KmtronicPort == 0) KmtronicPort = 12345;
        if (WavelogStationID == 0) WavelogStationID = 1;
        if (ClusterPollInterval == 0) ClusterPollInterval = 30;
        if (string.IsNullOrEmpty(ClusterAPIURL)) ClusterAPIURL = "https://api.wa0o.com/dxcache/spots";
        if (string.IsNullOrEmpty(RecordPath)) RecordPath = DefaultRecordPath;
        if (string.IsNullOrEmpty(PTTRecordPath)) PTTRecordPath = DefaultPTTRecordPath;
    }

    /// <summary>Validate configuration and return list of issues</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(RadioPort)) errors.Add("Radio port is required");
        int[] validBauds = [4800, 9600, 19200, 38400, 57600, 115200];
        if (!validBauds.Contains(RadioBaud)) errors.Add("Invalid baud rate");
        if (APIPort < 1 || APIPort > 65535) errors.Add("API port must be 1-65535");
        if (CatProxyPort < 1 || CatProxyPort > 65535) errors.Add("CAT proxy port must be 1-65535");
        if (AudioStreamPort < 1 || AudioStreamPort > 65535) errors.Add("Audio stream port must be 1-65535");
        int[] validSR = [8000, 11025, 22050, 44100, 48000];
        if (!validSR.Contains(RecordSampleRate)) errors.Add("Invalid sample rate");
        if (WavelogEnabled && string.IsNullOrEmpty(WavelogURL)) errors.Add("Wavelog URL required when enabled");
        if (WavelogEnabled && string.IsNullOrEmpty(WavelogAPIKey)) errors.Add("Wavelog API key required when enabled");
        return errors;
    }
}
