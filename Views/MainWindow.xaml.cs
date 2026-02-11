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
    private readonly DxClusterClient _cluster;
    private readonly FlexKnobController _flexknob;
    private readonly CwKeyer _cwKeyer;
    private readonly VoiceKeyer _voiceKeyer;
    private readonly SessionStats _stats = new();
    private readonly DispatcherTimer _updateTimer;

    private bool _running = true;
    private bool _forceExit;
    private string _lastBand = "";
    private string _lastMode = "";

    // Connection state tracking for auto-reconnect
    private bool _wasConnected;
    private DateTime _nextReconnectAttempt = DateTime.MaxValue; // Only reconnect after a drop
    private bool _reconnecting;
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(5);

    // PTT auto-record state (matching Go: lastPTTState, autoRecordActive, autoRecordTimer, etc.)
    private bool _lastPTTState;
    private bool _autoRecordActive;
    private DateTime _autoRecordTimer;
    private int _autoRecordSeconds;
    private long _autoRecordFreq;

    public MainWindow()
    {
        InitializeComponent();

        // Load config
        _config = Config.Load();

        // Init logger
        var logLevel = _config.LogLevel == "debug" ? Services.LogLevel.Debug : Services.LogLevel.Info;
        Logger.Init(logLevel, _config.LogToFile);
        Logger.Info("MAIN", "HamDeck v2.0 (C#) starting");

        // Validate config
        foreach (var err in _config.Validate())
            Logger.Warn("CONFIG", err);
        _config.ApplyDefaults();

        // PTT auto-record seconds (matching Go defaults)
        _autoRecordSeconds = _config.PTTRecordSeconds;
        if (_autoRecordSeconds <= 0) _autoRecordSeconds = 60;

        // Init services
        _radio = new RadioController();
        _recorder = new AudioRecorder(_radio, _config);
        _tgxl = new TgxlTuner(_radio, _config);
        _amp = new AmpTuner(_radio);
        _cluster = new DxClusterClient(_radio, _config);
        _flexknob = new FlexKnobController(_radio, _config);
        _cwKeyer = new CwKeyer(_radio);
        _voiceKeyer = new VoiceKeyer(_radio, _config);

        // FlexKnob - wire up UI callbacks and apply default step
        _flexknob.SetStep(_config.FlexknobDefaultStep);
        _flexknob.OnModeChanged += mode => Dispatcher.Invoke(() =>
        {
            FlexModeBtn.Content = mode;
            FlexStepBtn.Content = $"{_flexknob.StepDisplay} Hz";
        });
        _flexknob.OnAction += action => Dispatcher.Invoke(() =>
        {
            FlexKnobAction.Text = action;
            // Clear after 2 seconds
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

        // Init API server
        _api = new ApiServer(_radio, _recorder, _config, _tgxl, _amp);
        if (_config.APIEnabled) _api.Start();

        // Init Wavelog
        _wavelog = new WaveLogServer(_radio, _config);
        _wavelog.Start();

        // Populate port dropdown
        RefreshPorts();
        if (!string.IsNullOrEmpty(_config.RadioPort))
            PortSelect.SelectedItem = _config.RadioPort;

        // Setup update timer (200ms = 5 Hz)
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _updateTimer.Tick += UpdateTick;
        _updateTimer.Start();

        // Auto-connect
        Task.Run(AutoConnect);

        // Start ring buffer after delay
        Task.Run(async () =>
        {
            await Task.Delay(2000);
            _recorder.StartBuffer();
        });

        // FlexKnob
        if (_config.FlexknobEnabled)
            Task.Run(async () => { await Task.Delay(1000); _flexknob.Connect(); });

        // DX Cluster
        if (_config.ClusterEnabled)
            Task.Run(async () => { await Task.Delay(3000); _cluster.Connect(); });

        // Handle minimize to tray
        StateChanged += OnStateChanged;
        Closing += OnClosing;

        // Check for silent start
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
                ConnStatus.Text = "● Connected";
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
            _nextReconnectAttempt = DateTime.MaxValue; // Don't auto-reconnect after manual disconnect
            ConnStatus.Text = "● Disconnected";
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
            ConnStatus.Text = "● Connected";
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
                    ConnStatus.Text = "● Connected";
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

        // ===== DISCONNECTED STATE: detect drop, update UI, attempt reconnect =====
        if (!_radio.Connected)
        {
            // Transition: was connected → now disconnected
            if (_wasConnected)
            {
                _wasConnected = false;
                Logger.Warn("RADIO", "Radio disconnected — will auto-reconnect every {0}s", ReconnectInterval.TotalSeconds);

                // Update UI to show disconnected state
                ConnStatus.Text = "● Disconnected";
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

                // Stop PTT auto-record if active (radio is gone, save what we have)
                if (_autoRecordActive)
                {
                    Logger.Info("RECORD", "Radio lost — saving PTT recording");
                    StopPTTRecording();
                }
                _lastPTTState = false;

                // Schedule first reconnect attempt
                _nextReconnectAttempt = DateTime.UtcNow.Add(ReconnectInterval);
            }

            // Auto-reconnect: try periodically on a background thread
            if (!_reconnecting && !string.IsNullOrEmpty(_config.RadioPort)
                && DateTime.UtcNow >= _nextReconnectAttempt)
            {
                _reconnecting = true;
                _nextReconnectAttempt = DateTime.UtcNow.Add(ReconnectInterval);

                ConnStatus.Text = "● Reconnecting...";
                ConnStatus.Foreground = FindResource("WarningBrush") as SolidColorBrush;

                Task.Run(() =>
                {
                    try
                    {
                        // Release stale port handle before reconnecting
                        // (when radio drops, _port may still be open even though Connected=false)
                        _radio.Disconnect();

                        _radio.Connect(_config.RadioPort, _config.RadioBaud);
                        Dispatcher.Invoke(() =>
                        {
                            ConnStatus.Text = "● Connected";
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
                            ConnStatus.Text = "● Disconnected";
                            ConnStatus.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
                        });
                    }
                    finally
                    {
                        _reconnecting = false;
                    }
                });
            }

            // Still update stats display while disconnected
            StatsLabel.Text = $"Session: {_stats.SessionDuration} | QSY: {_stats.QSYCount} | TX: {_stats.PTTCount} | TX Time: {_stats.TXTimeDisplay}";
            return;
        }

        // Track that we're connected (for detecting future disconnects)
        _wasConnected = true;

        // ===== CONNECTED STATE: normal polling =====
        try
        {
            var freq = _radio.GetFreq();
            if (freq <= 0) return;

            // Format frequency: XX.XXX.XXX
            var mhz = freq / 1_000_000;
            var khz = (freq % 1_000_000) / 1_000;
            var hz = freq % 1_000;
            FreqLabel.Text = $"{mhz:D2}.{khz:D3}.{hz:D3}";

            // VFO-B freq for split display
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

            // Mode
            var mode = _radio.GetMode();
            ModeLabel.Text = mode;
            if (mode != _lastMode) { _stats.RecordModeChange(mode); _lastMode = mode; }

            // VFO
            VfoLabel.Text = $"VFO-{_radio.GetVFO()}";

            // Get PTT state
            var pttActive = _radio.GetTXStatus();

            // Check if tuner is active (don't trigger auto-record during tuning)
            var tunerActive = _tgxl.IsActive || _amp.IsActive;

            // TX stats tracking (but not during tuning)
            if (!tunerActive)
            {
                if (pttActive && !_lastPTTState) _stats.RecordTXStart();
                if (!pttActive && _lastPTTState) _stats.RecordTXEnd();
            }

            // ===== PTT AUTO-RECORD (matching Go main.go lines 832-868) =====
            // When PTT goes active and auto-record is enabled, start recording.
            // Each PTT press resets the timer. Recording stops on timeout or QSY.
            if (_config.PTTRecordEnabled && pttActive && !_lastPTTState && !tunerActive)
            {
                if (!_autoRecordActive)
                {
                    Logger.Info("RECORD", "PTT Auto-Record starting");
                    _recorder.Start();
                    _autoRecordActive = true;
                    _autoRecordFreq = freq;
                    RecordBtn.Content = "⏹ Stop";
                    RecordStatus.Text = "Auto-recording (PTT)";
                    RecordStatus.Foreground = FindResource("WarningBrush") as SolidColorBrush
                                              ?? FindResource("ErrorBrush") as SolidColorBrush;
                }
                // Reset timer on each PTT press (keep recording while active QSO)
                _autoRecordTimer = DateTime.UtcNow.AddSeconds(_autoRecordSeconds);
            }

            // Only update lastPTTState if not tuning (preserve state across tune cycles)
            if (!tunerActive)
                _lastPTTState = pttActive;

            // Check auto-record timeout or QSY
            if (_autoRecordActive && _autoRecordTimer != default)
            {
                if (DateTime.UtcNow > _autoRecordTimer)
                {
                    // Timer expired - no PTT for N seconds, save the QSO
                    Logger.Info("RECORD", "PTT timer expired, saving QSO");
                    StopPTTRecording();
                }
                else if (_autoRecordFreq > 0)
                {
                    // Check for QSY (frequency change beyond threshold)
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

            // TX indicator
            TxIndicator.Text = pttActive ? "● TX" : "";

            // S-Meter (only when RX)
            if (!pttActive)
            {
                var smeter = _radio.GetSMeter();
                SMeterBar.Value = smeter;
                SMeterLabel.Text = BandHelper.RawToSUnit(smeter);
            }

            // Power
            var power = _radio.GetPower();
            PowerLabel.Text = $"Power: {power}W";

            // Band change tracking
            var band = BandHelper.GetBand(freq);
            if (!string.IsNullOrEmpty(band) && band != _lastBand)
            {
                _stats.RecordBandChange(band);
                _lastBand = band;
            }

            // Stats
            StatsLabel.Text = $"Session: {_stats.SessionDuration} | QSY: {_stats.QSYCount} | TX: {_stats.PTTCount} | TX Time: {_stats.TXTimeDisplay}";

            // Ensure connection UI is correct (handles reconnect case)
            if (ConnectBtn.Content.ToString() != "Disconnect")
            {
                ConnStatus.Text = "● Connected";
                ConnStatus.Foreground = FindResource("SuccessBrush") as SolidColorBrush;
                ConnectBtn.Content = "Disconnect";
            }

            // FlexKnob connection indicator
            if (_flexknob.IsConnected)
            {
                FlexKnobStatus.Text = $"● {_config.FlexknobPort} | {_flexknob.ModeName} | Step: {_flexknob.StepDisplay} Hz";
                FlexKnobStatus.Foreground = FindResource("SuccessBrush") as SolidColorBrush;
            }
            else if (_config.FlexknobEnabled)
            {
                FlexKnobStatus.Text = "● Disconnected";
                FlexKnobStatus.Foreground = FindResource("DimTextBrush") as SolidColorBrush;
            }

            // Recording time display
            if (_recorder.IsRecording)
            {
                var status = _recorder.GetStatus();
                var elapsed = (double)status["duration"];

                if (_autoRecordActive && _autoRecordTimer != default)
                {
                    // Show countdown for PTT auto-record
                    var remaining = (_autoRecordTimer - DateTime.UtcNow).TotalSeconds;
                    if (remaining > 0)
                        RecordTime.Text = $"{elapsed:F0}s (-{remaining:F0}s)";
                    else
                        RecordTime.Text = $"{elapsed:F0}s";
                }
                else
                {
                    RecordTime.Text = $"{elapsed:F0}s";
                }

                RecordBtn.Content = "⏹ Stop";
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
                    RecordBtn.Content = "⏺ Record";
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

    /// <summary>Stop PTT auto-recording and save as QSO file (matching Go stopPTTRecording)</summary>
    private void StopPTTRecording()
    {
        RecordBtn.Content = "⏺ Record";
        RecordStatus.Text = "Saving QSO...";
        RecordStatus.Foreground = FindResource("DimTextBrush") as SolidColorBrush;

        _autoRecordActive = false;
        _autoRecordTimer = default;
        _stats.RecordQSO();

        // Build date-organized path: PTTRecordPath/2026-01/2026-01-15/
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

        // Clear status after 3 seconds
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

    private async void Band_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string band)
        {
            if (!BandHelper.BandFrequencies.TryGetValue(band, out var freq)) return;
            var mode = BandHelper.GetModeForFrequency(freq);
            _radio.SetFreq(freq);
            await Task.Delay(100);
            _radio.SetMode(mode);
        }
    }

    // ========== MODE ==========

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string mode)
            _radio.SetMode(mode);
    }

    // ========== VFO ==========

    private void VfoA_Click(object s, RoutedEventArgs e) => _radio.SetVFO("A");
    private void VfoB_Click(object s, RoutedEventArgs e) => _radio.SetVFO("B");
    private void VfoSwap_Click(object s, RoutedEventArgs e) => _radio.SwapVFO();
    private void VfoCopyAB_Click(object s, RoutedEventArgs e) => _radio.CopyVFO("A", "B");
    private void SplitToggle_Click(object s, RoutedEventArgs e)
    { var c = _radio.GetSplit(); _radio.SetSplit(!c); }

    // ========== POWER ==========

    private void Power_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string watts && int.TryParse(watts, out var w))
            _radio.SetPower(w);
    }

    // ========== TUNERS ==========

    private void Tune_Click(object s, RoutedEventArgs e) => _radio.StartTune();
    private void TgxlTune_Click(object s, RoutedEventArgs e) => _tgxl.Tune();
    private void AmpTune_Click(object s, RoutedEventArgs e) => _amp.Tune();

    // ========== FILTERS ==========

    private void ToggleNB_Click(object s, RoutedEventArgs e) { var c = _radio.GetNB(); _radio.SetNB(!c); }
    private void ToggleNR_Click(object s, RoutedEventArgs e) { var c = _radio.GetNR(); _radio.SetNR(!c); }
    private void ToggleNotch_Click(object s, RoutedEventArgs e) { var c = _radio.GetNotch(); _radio.SetNotch(!c); }
    private void ToggleVOX_Click(object s, RoutedEventArgs e) { var c = _radio.GetVOX(); _radio.SetVOX(!c); }
    private void ToggleComp_Click(object s, RoutedEventArgs e) { var c = _radio.GetComp(); _radio.SetComp(!c); }
    private void CyclePreamp_Click(object s, RoutedEventArgs e) => _radio.CyclePreamp();
    private void ToggleATT_Click(object s, RoutedEventArgs e) { var c = _radio.GetATT(); _radio.SetATT(!c); }
    private void ToggleLock_Click(object s, RoutedEventArgs e) { var c = _radio.GetLock(); _radio.SetLock(!c); }
    private void ToggleAntenna_Click(object s, RoutedEventArgs e) => _radio.ToggleAntenna();

    private void CycleAGC_Click(object s, RoutedEventArgs e)
    {
        var current = _radio.GetAGC();
        var next = current switch
        {
            "FAST" => "MID", "MID" => "SLOW", "SLOW" => "AUTO", "AUTO" => "OFF", _ => "FAST"
        };
        _radio.SetAGC(next);
    }

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

    private void FlexClearRIT_Click(object s, RoutedEventArgs e)
    {
        _radio.ClearRIT();
        Logger.Info("FLEXKNOB", "RIT cleared from UI");
    }

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
        // If PTT auto-record is active, stop it
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
        // Open whichever recording folder exists - prefer QSO path, fall back to record path
        var qsoPath = !string.IsNullOrEmpty(_config.PTTRecordPath)
            ? _config.PTTRecordPath : Config.DefaultPTTRecordPath;
        var recPath = !string.IsNullOrEmpty(_config.RecordPath)
            ? _config.RecordPath : Config.DefaultRecordPath;

        // Try QSO path first, then recording path
        var pathToOpen = Directory.Exists(qsoPath) ? qsoPath
                       : Directory.Exists(recPath) ? recPath
                       : qsoPath; // Create QSO path if neither exists

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
            var mode = BandHelper.GetModeForFrequency(hz);
            _radio.SetMode(mode);
            _radio.SetFreq(hz);
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
            _config = dlg.Config;
            _config.Save();
            Logger.Info("SETTINGS", "Configuration saved");
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

    private void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        ForceExit();
    }

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
        _api.Dispose();
        _radio.Disconnect();
        TrayIcon.Dispose();
        Logger.Info("MAIN", "Cleanup complete");
        Logger.Close();
    }
}
