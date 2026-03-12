using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
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
    private Config _config;
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
    private AudioStreamer? _streamer;
    private AudioTransmitter? _txAudio;

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

    public MainWindow()
    {
        InitializeComponent();

        _config = Config.Load();

        var logLevel = _config.LogLevel == "debug" ? Services.LogLevel.Debug : Services.LogLevel.Info;
        Logger.Init(logLevel, _config.LogToFile);
        Logger.Info("MAIN", "HamDeck v3.3 (C#) starting");

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
            // Status text updated in UpdateTick via FlexKnobStatus
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

        // Audio streamer (captures audio, WebSocket served via dashboard port)
        _streamer = new AudioStreamer(_config);
        _streamer.Start();
        _recorder.Streamer = _streamer;

        // Web authentication for dashboard control
        AuthService? auth = null;
        if (_config.WebUsers.Count > 0 || !string.IsNullOrEmpty(_config.WebPasswordHash))
        {
            auth = new AuthService(_config.WebSessionTimeout);

            foreach (var u in _config.WebUsers)
                auth.AddUser(u.Username, u.PasswordHash, u.IsAdmin);

            if (_config.WebUsers.Count == 0 && !string.IsNullOrEmpty(_config.WebPasswordHash))
            {
                auth.AddUser(_config.WebUsername, _config.WebPasswordHash, true);
                Logger.Info("MAIN", "Migrated legacy single-user auth to multi-user");
            }

            Logger.Info("MAIN", "Web authentication enabled ({0} users)", _config.WebUsers.Count > 0 ? _config.WebUsers.Count : 1);
        }
        else
        {
            Logger.Warn("MAIN", "Web password not set — dashboard will prompt for setup on first visit");
        }

        if (_config.TxAudioEnabled)
        {
            _txAudio = new AudioTransmitter(_config);
            Logger.Info("MAIN", "TX audio enabled (device: {0})",
                _config.TxAudioDevice >= 0 ? _config.TxAudioDevice.ToString() : "auto-detect");
        }

        _api = new ApiServer(_radio, _recorder, _config, _tgxl, _amp, _kmtronic, streamer: _streamer, auth: auth, txAudio: _txAudio, flexknob: _flexknob);
        if (_config.APIEnabled) _api.Start();

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

        try
        {
            _radio.Connect(port, _config.RadioBaud);
            _config.RadioPort = port;
            _config.Save();
            _wasConnected = true;
            ConnStatus.Text = "\u25CF Connected";
            ConnStatus.Foreground = FindResource("SuccessBrush") as SolidColorBrush;
            ConnectBtn.Content = "Disconnect";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Connection failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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

        if (Environment.TickCount64 - _radio.LastProxyActivityMs < 300) return;
        if (_flexknob.IsActive) return;

        // ===== DISCONNECTED STATE =====
        if (!_radio.Connected)
        {
            if (_wasConnected)
            {
                _wasConnected = false;
                Logger.Warn("RADIO", "Radio disconnected — will auto-reconnect every {0}s", ReconnectInterval.TotalSeconds);

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
                    Logger.Info("RECORD", "Radio lost — saving PTT recording");
                    StopPTTRecording();
                }
                _lastPTTState = false;
                _nextReconnectAttempt = DateTime.UtcNow.Add(ReconnectInterval);
                _streamer?.UpdateStatus(0, "", "", 0, false, false);
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
        try
        {
            var freq = _radio.GetFreq();
            if (freq <= 0) return;

            var mhz = freq / 1_000_000;
            var khz = (freq % 1_000_000) / 1_000;
            var hz = freq % 1_000;
            FreqLabel.Text = $"{mhz:D2}.{khz:D3}.{hz:D3}";

            if (_radio.GetSplit())
            {
                var freqB = _radio.GetFreqB();
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

            var mode = _radio.GetMode();
            ModeLabel.Text = mode;
            if (mode != _lastMode) { _stats.RecordModeChange(mode); _lastMode = mode; }

            VfoLabel.Text = $"VFO-{_radio.GetVFO()}";

            var pttActive = _radio.GetTXStatus();
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
                var smeter = _radio.GetSMeter();
                SMeterBar.Value = smeter;
                SMeterLabel.Text = BandHelper.RawToSUnit(smeter);
            }

            var power = _radio.GetPower();
            PowerLabel.Text = $"Power: {power}W";

            var band = BandHelper.GetBand(freq);
            if (!string.IsNullOrEmpty(band) && band != _lastBand)
            {
                _stats.RecordBandChange(band);
                _lastBand = band;
            }

            _streamer?.UpdateStatus(freq, mode, band, power, pttActive, true);

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
                var status = _recorder.GetStatus();
                var elapsed = (double)status["duration"];
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

    // ========== FLEXKNOB ==========

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

    // ========== DASHBOARD ==========

    private void OpenDashboard_Click(object sender, RoutedEventArgs e)
    {
        var url = "http://localhost:5002";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    // ========== SETTINGS ==========

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsDialog(_config) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _config = dlg.Config;
            _config.Save();
            Logger.Info("SETTINGS", "Configuration saved");

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
        _streamer?.Dispose();
        _txAudio?.Dispose();
        _api.Dispose();
        _radio.Disconnect();
        TrayIcon.Dispose();
        Logger.Info("MAIN", "Cleanup complete");
        Logger.Close();
    }
}
