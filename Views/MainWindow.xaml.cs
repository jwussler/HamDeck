using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HamDeck.Helpers;
using HamDeck.Models;
using HamDeck.Services;

namespace HamDeck.Views;

public partial class MainWindow : Window
{
    // WARNING FIX: _config is now readonly — all services hold a reference to this
    // same object. Use _config.CopyFrom(newConfig) in Settings_Click so every service
    // automatically sees updated values without a restart. Reassigning _config = dlg.Config
    // broke ClusterPollInterval, PTTRecordSeconds, AudioDevice, etc. for live services.
    private readonly Config _config;
    private readonly RadioController _radio;
    private readonly AudioRecorder _recorder;
    private readonly ApiServer _api;
    private readonly WaveLogServer _wavelog;
    private readonly TgxlTuner _tgxl;
    private readonly AmpTuner _amp;
    private readonly KmtronicService? _kmtronic;
    private readonly DxClusterClient _cluster;
    private readonly FlexKnobController _flexknob;
    private readonly CwKeyer _cwKeyer;
    private readonly VoiceKeyer _voiceKeyer;
    private readonly SessionStats _stats = new();
    private readonly DispatcherTimer _updateTimer;
    private TcpCatProxy? _catProxy;

    private bool _running = true;
    private bool _forceExit;
    private string _lastBand = "";
    private string _lastMode = "";

    // Connection state tracking for auto-reconnect
    private bool _wasConnected;
    private DateTime _nextReconnectAttempt = DateTime.MaxValue;
    private bool _reconnecting;
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(5);

    // PTT auto-record state
    private bool _lastPTTState;
    private bool _autoRecordActive;
    private DateTime _autoRecordTimer;
    private int _autoRecordSeconds;
    private long _autoRecordFreq;

    // v3: All GUI buttons route through the API so behavior matches Stream Deck
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private void Api(string path) => Task.Run(async () =>
    {
        try { await _http.GetAsync($"http://localhost:{_config.APIPort}{path}"); }
        catch (Exception ex) { Logger.Debug("API-UI", "Call failed: {0} — {1}", path, ex.Message); }
    });

    public MainWindow()
    {
        InitializeComponent();

        _config = Config.Load();

        var logLevel = _config.LogLevel == "debug" ? Services.LogLevel.Debug : Services.LogLevel.Info;
        Logger.Init(logLevel, _config.LogToFile);
        Logger.Info("MAIN", "HamDeck v2.0 (C#) starting");

        // NOTE FIX: Warn the user if a corrupt config was detected on load.
        // Config.Load() backs it up and returns defaults; we surface that here so the
        // user isn't silently running with defaults they didn't expect.
        if (_config.WasLoadedFromCorruptFile)
        {
            Logger.Warn("CONFIG", "Config file was corrupt — loaded defaults. A backup was saved to: {0}.corrupt_*", Config.ConfigFile);
            Dispatcher.BeginInvoke(() =>
            {
                MessageBox.Show(
                    "HamDeck could not read your config file and has loaded defaults.\n\n" +
                    "A backup of the corrupt file was saved next to config.json.\n\n" +
                    "Please review your settings.",
                    "Config Load Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            });
        }

        foreach (var err in _config.Validate())
            Logger.Warn("CONFIG", err);
        _config.ApplyDefaults();

        _autoRecordSeconds = _config.PTTRecordSeconds;
        if (_autoRecordSeconds <= 0) _autoRecordSeconds = 60;

        _radio = new RadioController();
        _recorder = new AudioRecorder(_radio, _config);
        _tgxl = new TgxlTuner(_radio, _config);
        _amp = new AmpTuner(_radio);
        _kmtronic = _config.KmtronicEnabled
            ? new KmtronicService(_config.KmtronicHost, _config.KmtronicPort)
            : null;

        if (_kmtronic != null)
            Logger.Info("MAIN", "KMTronic relay at {0}:{1}", _config.KmtronicHost, _config.KmtronicPort);
        _cluster = new DxClusterClient(_radio, _config);
        _flexknob = new FlexKnobController(_radio, _config);
        _cwKeyer = new CwKeyer(_radio);
        _voiceKeyer = new VoiceKeyer(_radio, _config);

        _flexknob.SetStep(_config.FlexknobDefaultStep);
        _flexknob.OnModeChanged += mode => Dispatcher.Invoke(() =>
        {
            FlexModeBtn.Content = mode;
            FlexStepBtn.Content = $"{_flexknob.StepDisplay} Hz";
        });
        _flexknob.OnAction += action => Dispatcher.Invoke(() =>
        {
            FlexKnobAction.Text = action;
            Task.Run(async () =>
            {
                await Task.Delay(2000);
                Dispatcher.Invoke(() => { if (FlexKnobAction.Text == action) FlexKnobAction.Text = ""; });
            });
        });
        _flexknob.OnStatusChanged += status => Dispatcher.Invoke(() =>
        {
            FlexKnobStatus.Text = status;
            FlexConnBtn.Content = _flexknob.IsConnected ? "Disconnect" : "Connect";
        });

        _api = new ApiServer(_radio, _recorder, _config, _tgxl, _amp, _kmtronic);
        if (_config.APIEnabled) _api.Start();

        // TCP CAT Proxy for N1MM and external loggers
        if (_config.CatProxyEnabled)
        {
            _catProxy = new TcpCatProxy(_radio, _config.CatProxyPort);
            _catProxy.Start();
        }

        _wavelog = new WaveLogServer(_radio, _config);
        _wavelog.Start();

        RefreshPorts();
        if (!string.IsNullOrEmpty(_config.RadioPort))
            PortSelect.SelectedItem = _config.RadioPort;

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _updateTimer.Tick += UpdateTick;
        _updateTimer.Start();

        Task.Run(AutoConnect);

        Task.Run(async () =>
        {
            await Task.Delay(2000);
            _recorder.StartBuffer();
        });

        if (_config.FlexknobEnabled)
            Task.Run(async () => { await Task.Delay(1000); _flexknob.Connect(); });

        if (_config.ClusterEnabled)
            Task.Run(async () => { await Task.Delay(3000); _cluster.Connect(); });

        StateChanged += OnStateChanged;
        Closing += OnClosing;

        if (Application.Current.Properties["StartSilent"] is true || _config.StartMinimized)
        {
            WindowState = WindowState.Minimized;
            if (_config.MinimizeToTray) Hide();
        }
    }

    // ========== CONNECTION ==========

    private void RefreshPorts()
    {
        var ports = SerialPort.GetPortNames();
        PortSelect.Items.Clear();
        foreach (var p in ports) PortSelect.Items.Add(p);
        if (ports.Length == 0)
            for (int i = 1; i <= 16; i++) PortSelect.Items.Add($"COM{i}");
    }

    private void AutoConnect()
    {
        if (string.IsNullOrEmpty(_config.RadioPort)) return;
        Thread.Sleep(500);
        try
        {
            _radio.Connect(_config.RadioPort, _config.RadioBaud);
            Dispatcher.Invoke(() =>
            {
                _wasConnected = true;
                ConnStatus.Text = "\u25CF Connected";
                ConnStatus.Foreground = FindResource("SuccessBrush") as SolidColorBrush;
                ConnectBtn.Content = "Disconnect";
            });
        }
        catch (Exception ex)
        {
            Logger.Warn("MAIN", "Auto-connect failed: {0}", ex.Message);
        }
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_radio.Connected)
        {
            _radio.Disconnect();
            _wasConnected = false;
            _nextReconnectAttempt = DateTime.MaxValue;
            ConnStatus.Text = "\u25CF Disconnected";
            ConnStatus.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
            ConnectBtn.Content = "Connect";
            return;
        }

        var port = PortSelect.SelectedItem?.ToString();
        if (string.IsNullOrEmpty(port)) { MessageBox.Show("Select a COM port"); return; }

        // WARNING FIX: Move Connect() off the UI thread. _radio.Connect() opens a COM
        // port with Thread.Sleep(100) + GetFreq() (up to 200ms timeout), freezing the
        // WPF dispatcher thread on slow or absent ports. Disable UI for feedback.
        ConnectBtn.IsEnabled = false;
        ConnStatus.Text = "\u25CF Connecting...";
        ConnStatus.Foreground = FindResource("WarningBrush") as SolidColorBrush;

        Task.Run(() =>
        {
            try
            {
                _radio.Connect(port, _config.RadioBaud);
                Dispatcher.Invoke(() =>
                {
                    _config.RadioPort = port;
                    _config.Save();
                    _wasConnected = true;
                    ConnStatus.Text = "\u25CF Connected";
                    ConnStatus.Foreground = FindResource("SuccessBrush") as SolidColorBrush;
                    ConnectBtn.Content = "Disconnect";
                    ConnectBtn.IsEnabled = true;
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    ConnStatus.Text = "\u25CF Disconnected";
                    ConnStatus.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
                    ConnectBtn.Content = "Connect";
                    ConnectBtn.IsEnabled = true;
                    MessageBox.Show($"Connection failed: {ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
        });
    }

    private void AutoDetect_Click(object sender, RoutedEventArgs e)
    {
        AutoDetectBtn.IsEnabled = false;
        Task.Run(() =>
        {
            var port = _radio.AutoDetect(_config.RadioBaud);
            Dispatcher.Invoke(() =>
            {
                AutoDetectBtn.IsEnabled = true;
                if (port != null)
                {
                    PortSelect.SelectedItem = port;
                    _config.RadioPort = port;
                    _config.Save();
                    _wasConnected = true;
                    ConnStatus.Text = "\u25CF Connected";
                    ConnStatus.Foreground = FindResource("SuccessBrush") as SolidColorBrush;
                    ConnectBtn.Content = "Disconnect";
                }
                else
                {
                    MessageBox.Show("No radio found on any COM port.", "Auto-detect",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            });
        });
    }

    // ========== UPDATE LOOP ==========

    private void UpdateTick(object? sender, EventArgs e)
    {
        if (!_running) return;

        // ===== DISCONNECTED STATE =====
        if (!_radio.Connected)
        {
            if (_wasConnected)
            {
                _wasConnected = false;
                Logger.Warn("RADIO", "Radio disconnected \u2014 will auto-reconnect every {0}s", ReconnectInterval.TotalSeconds);

                ConnStatus.Text = "\u25CF Disconnected";
                ConnStatus.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
                ConnectBtn.Content = "Connect";
                TxIndicator.Text = "";
                SplitIndicator.Text = "";
                FreqLabelB.Text = "";
                ModeLabel.Text = "---";
                VfoLabel.Text = "VFO-?";
                SMeterBar.Value = 0;
                SMeterLabel.Text = "S0";
                PowerLabel.Text = "Power: ---";

                if (_autoRecordActive)
                {
                    Logger.Info("RECORD", "Radio lost \u2014 saving PTT recording");
                    StopPTTRecording();
                }
                _lastPTTState = false;
                _nextReconnectAttempt = DateTime.UtcNow.Add(ReconnectInterval);
            }

            if (!_reconnecting && !string.IsNullOrEmpty(_config.RadioPort)
                && DateTime.UtcNow >= _nextReconnectAttempt)
            {
                _reconnecting = true;
                _nextReconnectAttempt = DateTime.UtcNow.Add(ReconnectInterval);
                ConnStatus.Text = "\u25CF Reconnecting...";
                ConnStatus.Foreground = FindResource("WarningBrush") as SolidColorBrush;

                Task.Run(() =>
                {
                    try
                    {
                        _radio.Disconnect();
                        _radio.Connect(_config.RadioPort, _config.RadioBaud);
                        Dispatcher.Invoke(() =>
                        {
                            ConnStatus.Text = "\u25CF Connected";
                            ConnStatus.Foreground = FindResource("SuccessBrush") as SolidColorBrush;
                            ConnectBtn.Content = "Disconnect";
                            _wasConnected = true;
                            Logger.Info("RADIO", "Auto-reconnected on {0}", _config.RadioPort);
                        });
                    }
                    catch
                    {
                        Dispatcher.Invoke(() =>
                        {
                            ConnStatus.Text = "\u25CF Disconnected";
                            ConnStatus.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
                        });
                    }
                    finally
                    {
                        _reconnecting = false;
                    }
                });
            }

            StatsLabel.Text = $"Session: {_stats.SessionDuration} | QSY: {_stats.QSYCount} | TX: {_stats.PTTCount} | TX Time: {_stats.TXTimeDisplay}";
            return;
        }

        _wasConnected = true;

        // ===== CONNECTED STATE =====
        // When a proxy client (N1MM etc.) is actively polling the radio, it saturates
        // the serial lock. Instead of competing (and losing), we read the cached values
        // that SendRaw already parsed from proxy traffic. Zero serial queries needed.
        bool proxyActive = _radio.ProxyIsActive;

        try
        {
            long freq;
            bool split;
            string mode;
            string vfo;
            bool pttActive;
            int smeter;
            int power;

            if (proxyActive)
            {
                // ── Proxy-fed: use cached values (no serial I/O) ──
                freq = _radio.LastFrequency;
                if (freq <= 0) return;
                split = _radio.LastSplit;
                mode = _radio.LastMode;
                vfo = _radio.LastVFO;
                pttActive = _radio.LastTXState;
                smeter = _radio.LastSMeter;
                power = _radio.LastPower;
            }
            else
            {
                // ── Normal polling: query the radio directly ──
                freq = _radio.GetFreq();
                if (freq <= 0) return;
                split = _radio.GetSplit();
                mode = _radio.GetMode();
                vfo = _radio.GetVFO();
                pttActive = _radio.GetTXStatus();
                smeter = !pttActive ? _radio.GetSMeter() : 0;
                power = _radio.GetPower();
            }

            // ── Update frequency display ──
            var mhz = freq / 1_000_000;
            var khz = (freq % 1_000_000) / 1_000;
            var hz = freq % 1_000;
            FreqLabel.Text = $"{mhz:D2}.{khz:D3}.{hz:D3}";

            if (split)
            {
                var freqB = proxyActive ? _radio.LastFreqB : _radio.GetFreqB();
                if (freqB > 0)
                {
                    var mB = freqB / 1_000_000; var kB = (freqB % 1_000_000) / 1_000; var hB = freqB % 1_000;
                    FreqLabelB.Text = $"TX: {mB:D2}.{kB:D3}.{hB:D3}";
                }
                SplitIndicator.Text = "SPLIT";
            }
            else
            {
                FreqLabelB.Text = "";
                SplitIndicator.Text = "";
            }

            ModeLabel.Text = mode;
            if (mode != _lastMode) { _stats.RecordModeChange(mode); _lastMode = mode; }

            VfoLabel.Text = $"VFO-{vfo}";

            var tunerActive = _tgxl.IsActive || _amp.IsActive;

            if (!tunerActive)
            {
                if (pttActive && !_lastPTTState) _stats.RecordTXStart();
                if (!pttActive && _lastPTTState) _stats.RecordTXEnd();
            }

            // ===== PTT AUTO-RECORD =====
            if (_config.PTTRecordEnabled && pttActive && !_lastPTTState && !tunerActive)
            {
                if (!_autoRecordActive)
                {
                    Logger.Info("RECORD", "PTT Auto-Record starting");
                    _recorder.Start();
                    _autoRecordActive = true;
                    _autoRecordFreq = freq;
                    RecordBtn.Content = "\u23F9 Stop";
                    RecordStatus.Text = "Auto-recording (PTT)";
                    RecordStatus.Foreground = FindResource("WarningBrush") as SolidColorBrush
                                              ?? FindResource("ErrorBrush") as SolidColorBrush;
                }
                _autoRecordTimer = DateTime.UtcNow.AddSeconds(_autoRecordSeconds);
            }

            if (!tunerActive)
                _lastPTTState = pttActive;

            if (_autoRecordActive && _autoRecordTimer != default)
            {
                if (DateTime.UtcNow > _autoRecordTimer)
                {
                    Logger.Info("RECORD", "PTT timer expired, saving QSO");
                    StopPTTRecording();
                }
                else if (_autoRecordFreq > 0)
                {
                    var qsyThreshold = (long)_config.PTTQSYThresholdKHz * 1000;
                    if (qsyThreshold <= 0) qsyThreshold = 10000;
                    var freqDiff = Math.Abs(freq - _autoRecordFreq);
                    if (freqDiff > qsyThreshold)
                    {
                        Logger.Info("RECORD", "QSY detected ({0} Hz change), saving QSO", freqDiff);
                        StopPTTRecording();
                    }
                }
            }

            TxIndicator.Text = pttActive ? "\u25CF TX" : "";

            if (!pttActive)
            {
                SMeterBar.Value = smeter;
                SMeterLabel.Text = BandHelper.RawToSUnit(smeter);
            }

            PowerLabel.Text = $"Power: {power}W";

            var band = BandHelper.GetBand(freq);
            if (!string.IsNullOrEmpty(band) && band != _lastBand)
            {
                _stats.RecordBandChange(band);
                _lastBand = band;
            }

            StatsLabel.Text = $"Session: {_stats.SessionDuration} | QSY: {_stats.QSYCount} | TX: {_stats.PTTCount} | TX Time: {_stats.TXTimeDisplay}";

            if (ConnectBtn.Content.ToString() != "Disconnect")
            {
                ConnStatus.Text = "\u25CF Connected";
                ConnStatus.Foreground = FindResource("SuccessBrush") as SolidColorBrush;
                ConnectBtn.Content = "Disconnect";
            }

            if (_flexknob.IsConnected)
            {
                FlexKnobStatus.Text = $"\u25CF {_config.FlexknobPort} | {_flexknob.ModeName} | Step: {_flexknob.StepDisplay} Hz";
                FlexKnobStatus.Foreground = FindResource("SuccessBrush") as SolidColorBrush;
            }
            else if (_config.FlexknobEnabled)
            {
                FlexKnobStatus.Text = "\u25CF Disconnected";
                FlexKnobStatus.Foreground = FindResource("DimTextBrush") as SolidColorBrush;
            }

            if (_recorder.IsRecording)
            {
                var recStatus = _recorder.GetStatus();
                var elapsed = (double)recStatus["duration"];
                if (_autoRecordActive && _autoRecordTimer != default)
                {
                    var remaining = (_autoRecordTimer - DateTime.UtcNow).TotalSeconds;
                    RecordTime.Text = remaining > 0 ? $"{elapsed:F0}s (-{remaining:F0}s)" : $"{elapsed:F0}s";
                }
                else
                {
                    RecordTime.Text = $"{elapsed:F0}s";
                }
                RecordBtn.Content = "\u23F9 Stop";
                if (!_autoRecordActive)
                {
                    RecordStatus.Text = "Recording...";
                    RecordStatus.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
                }
            }
            else
            {
                if (!_autoRecordActive)
                {
                    RecordBtn.Content = "\u23FA Record";
                    RecordStatus.Text = _recorder.IsBuffering ? "Buffer ready" : "Buffer off";
                    RecordStatus.Foreground = FindResource("DimTextBrush") as SolidColorBrush;
                    RecordTime.Text = "";
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug("UI", "Update error: {0}", ex.Message);
        }
    }

    // ========== PTT AUTO-RECORD HELPERS ==========

    private void StopPTTRecording()
    {
        RecordBtn.Content = "\u23FA Record";
        RecordStatus.Text = "Saving QSO...";
        RecordStatus.Foreground = FindResource("DimTextBrush") as SolidColorBrush;

        _autoRecordActive = false;
        _autoRecordTimer = default;
        _stats.RecordQSO();

        var pttBasePath = !string.IsNullOrEmpty(_config.PTTRecordPath)
            ? _config.PTTRecordPath
            : Config.DefaultPTTRecordPath;
        var now = DateTime.UtcNow;
        var monthFolder = now.ToString("yyyy-MM");
        var dateFolder = now.ToString("yyyy-MM-dd");
        var pttPath = Path.Combine(pttBasePath, monthFolder, dateFolder);

        var filename = _recorder.StopWithPath(pttPath, "qso");

        if (filename != null)
        {
            RecordStatus.Text = "QSO saved!";
            Logger.Info("RECORD", "QSO saved: {0}", filename);
        }
        else
        {
            RecordStatus.Text = "Save error";
        }

        RecordTime.Text = "";

        Task.Run(async () =>
        {
            await Task.Delay(3000);
            Dispatcher.Invoke(() =>
            {
                if (!_recorder.IsRecording && !_autoRecordActive)
                {
                    RecordStatus.Text = _recorder.IsBuffering ? "Buffer ready" : "Buffer off";
                    RecordStatus.Foreground = FindResource("DimTextBrush") as SolidColorBrush;
                }
            });
        });
    }

    // ========== BAND ==========

    private void Band_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string band)
            Api($"/api/band/{band}");
    }

    // ========== MODE ==========

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string mode)
            Api($"/api/mode/{mode.ToLower()}");
    }

    // ========== VFO ==========

    private void VfoA_Click(object s, RoutedEventArgs e) => Api("/api/vfo/a");
    private void VfoB_Click(object s, RoutedEventArgs e) => Api("/api/vfo/b");
    private void VfoSwap_Click(object s, RoutedEventArgs e) => Api("/api/vfo/swap");
    private void VfoCopyAB_Click(object s, RoutedEventArgs e) => Api("/api/vfo-copy/a2b");
    private void SplitToggle_Click(object s, RoutedEventArgs e) => Api("/api/split/toggle");

    // ========== POWER ==========

    private void Power_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string watts)
            Api($"/api/power/set/{watts}");
    }

    // ========== TUNERS ==========

    private void Tune_Click(object s, RoutedEventArgs e) => Api("/api/tune");
    private void TgxlTune_Click(object s, RoutedEventArgs e) => Api("/api/tune/tgxl");
    private void AmpTune_Click(object s, RoutedEventArgs e) => Api("/api/tune/amp");

    // ========== FILTERS ==========

    private void ToggleNB_Click(object s, RoutedEventArgs e) => Api("/api/toggle/nb");
    private void ToggleNR_Click(object s, RoutedEventArgs e) => Api("/api/toggle/nr");
    private void ToggleNotch_Click(object s, RoutedEventArgs e) => Api("/api/toggle/notch");
    private void ToggleVOX_Click(object s, RoutedEventArgs e) => Api("/api/vox/toggle");
    private void ToggleComp_Click(object s, RoutedEventArgs e) => Api("/api/comp/toggle");
    private void CyclePreamp_Click(object s, RoutedEventArgs e) => Api("/api/preamp/cycle");
    private void ToggleATT_Click(object s, RoutedEventArgs e) => Api("/api/att/toggle");
    private void ToggleLock_Click(object s, RoutedEventArgs e) => Api("/api/toggle/lock");
    private void ToggleAntenna_Click(object s, RoutedEventArgs e) => Api("/api/ant/toggle");
    private void Ant1_Click(object s, RoutedEventArgs e) => Api("/api/ant/1");
    private void Ant2_Click(object s, RoutedEventArgs e) => Api("/api/ant/2");
    private void RxAnt_Click(object s, RoutedEventArgs e) => Api("/api/rxant/cycle");

    private void CycleAGC_Click(object s, RoutedEventArgs e) => Api("/api/agc/cycle");

    // ========== FLEXKNOB ==========

    private void FlexMode_Click(object s, RoutedEventArgs e)
    {
        _flexknob.CycleMode();
        FlexModeBtn.Content = _flexknob.ModeName;
        FlexStepBtn.Content = $"{_flexknob.StepDisplay} Hz";
    }

    private void FlexStep_Click(object s, RoutedEventArgs e)
    {
        _flexknob.CycleStep();
        FlexStepBtn.Content = $"{_flexknob.StepDisplay} Hz";
    }

    private void FlexSetVol_Click(object s, RoutedEventArgs e)
    {
        _flexknob.SetMode(FlexKnobController.KnobMode.Volume);
        FlexModeBtn.Content = _flexknob.ModeName;
    }

    private void FlexSetRIT_Click(object s, RoutedEventArgs e)
    {
        _flexknob.SetMode(FlexKnobController.KnobMode.RIT);
        FlexModeBtn.Content = _flexknob.ModeName;
    }

    private void FlexClearRIT_Click(object s, RoutedEventArgs e) => Api("/api/rit/clear");

    private void FlexConnect_Click(object s, RoutedEventArgs e)
    {
        if (_flexknob.IsConnected)
        {
            _flexknob.Disconnect();
            FlexConnBtn.Content = "Connect";
            FlexKnobStatus.Text = "Disconnected";
        }
        else
        {
            _flexknob.Connect();
            FlexConnBtn.Content = _flexknob.IsConnected ? "Disconnect" : "Connect";
            FlexKnobStatus.Text = _flexknob.IsConnected ? "Connected" : "Failed";
        }
    }

    // ========== RECORDING ==========

    private void RecordToggle_Click(object s, RoutedEventArgs e)
    {
        if (_autoRecordActive)
        {
            StopPTTRecording();
            return;
        }
        if (_recorder.IsRecording) _recorder.Stop();
        else _recorder.Start();
    }

    private void SaveReplay_Click(object s, RoutedEventArgs e)
    {
        var file = _recorder.SaveReplay();
        if (file != null) Logger.Info("UI", "Replay saved: {0}", file);
        else MessageBox.Show("No replay data in buffer", "Replay");
    }

    private void OpenRecordFolder_Click(object s, RoutedEventArgs e)
    {
        var qsoPath = !string.IsNullOrEmpty(_config.PTTRecordPath)
            ? _config.PTTRecordPath : Config.DefaultPTTRecordPath;
        var recPath = !string.IsNullOrEmpty(_config.RecordPath)
            ? _config.RecordPath : Config.DefaultRecordPath;

        var pathToOpen = Directory.Exists(qsoPath) ? qsoPath
                       : Directory.Exists(recPath) ? recPath
                       : qsoPath;

        Directory.CreateDirectory(pathToOpen);
        System.Diagnostics.Process.Start("explorer.exe", pathToOpen);
    }

    // ========== FREQUENCY ENTRY ==========

    private void FreqEntry_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) FreqGo_Click(sender, e);
    }

    private void FreqGo_Click(object s, RoutedEventArgs e)
    {
        var hz = FrequencyHelper.Parse(FreqEntry.Text);
        if (hz > 0)
        {
            Api($"/api/freq/set/{hz}");
            FreqEntry.Clear();
        }
    }

    // ========== DX CLUSTER ==========

    private DxClusterWindow? _clusterWindow;

    private void DxCluster_Click(object sender, RoutedEventArgs e)
    {
        if (_clusterWindow != null)
        {
            _clusterWindow.Activate();
            return;
        }
        _clusterWindow = new DxClusterWindow(_cluster, _radio, _config) { Owner = this };
        _clusterWindow.Closed += (s, a) => _clusterWindow = null;
        _clusterWindow.Show();
    }

    // ========== SETTINGS ==========

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsDialog(_config) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            // WARNING FIX: Use CopyFrom() to update _config in-place instead of
            // reassigning the field. All services (_recorder, _cluster, _flexknob,
            // _wavelog, etc.) hold a direct reference to _config — reassigning here
            // left them all pointing at the old object, so changes to AudioDevice,
            // ClusterPollInterval, PTTRecordSeconds, etc. required an app restart.
            _config.CopyFrom(dlg.Config);
            _config.Save();
            Logger.Info("SETTINGS", "Configuration saved");

            // Restart CAT proxy if its port setting changed
            _catProxy?.Dispose();
            _catProxy = null;
            if (_config.CatProxyEnabled)
            {
                _catProxy = new TcpCatProxy(_radio, _config.CatProxyPort);
                _catProxy.Start();
            }
        }
    }

    // ========== SYSTEM TRAY ==========

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _config.MinimizeToTray)
            Hide();
    }

    private void TrayIcon_DoubleClick(object sender, RoutedEventArgs e)
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void TrayShow_Click(object sender, RoutedEventArgs e)
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void TrayExit_Click(object sender, RoutedEventArgs e) => ForceExit();

    private void ForceExit()
    {
        _forceExit = true;
        _running = false;
        Cleanup();
        Application.Current.Shutdown();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_config.MinimizeToTray && !_forceExit)
        {
            e.Cancel = true;
            WindowState = WindowState.Minimized;
            Hide();
            return;
        }
        _running = false;
        Cleanup();
    }

    private void Cleanup()
    {
        _updateTimer.Stop();
        _stats.Save();
        _recorder.Cleanup();
        _voiceKeyer.Cleanup();
        _flexknob.Disconnect();
        _cluster.Disconnect();
        _catProxy?.Dispose();
        _kmtronic?.Dispose();
        _api.Dispose();
        _radio.Disconnect();
        TrayIcon.Dispose();
        Logger.Info("MAIN", "Cleanup complete");
        Logger.Close();
    }
}
