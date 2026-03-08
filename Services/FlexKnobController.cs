using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HamDeck.Models;

namespace HamDeck.Services;

/// <summary>
/// FlexKnob USB controller - reads encoder/button input via serial port
/// and translates to radio controls. Matches Go implementation behavior.
///
/// Supports two protocols:
/// 1) FlexKnob native (semicolon-delimited): U, U02, D, D02, X1S, X1L, X2S, etc.
/// 2) Legacy (newline-delimited): E+N, E-N, +N, -N, L, R, CW, CCW, B1-B4
/// </summary>
public class FlexKnobController : IDisposable
{
    private readonly RadioController _radio;
    private readonly Config _config;
    private SerialPort? _port;
    private Thread? _readThread;
    private volatile bool _running;
    private readonly object _lock = new();

    public bool IsConnected { get; private set; }

    // Modes
    public enum KnobMode { Frequency, Volume, RIT, Custom }
    private static readonly string[] ModeNames = ["FREQ", "VOL", "RIT", "CUSTOM"];
    public KnobMode Mode { get; private set; } = KnobMode.Frequency;
    public string ModeName => ModeNames[(int)Mode];

    // Step sizes (Hz)
    private static readonly int[] StepSizes = [10, 50, 100, 500, 1000, 5000, 10000];
    private int _stepIndex = 2; // Default 100 Hz
    public int StepHz => StepSizes[_stepIndex];
    public string StepDisplay => StepHz >= 1000 ? $"{StepHz / 1000}k" : $"{StepHz}";

    // UI callbacks
    public event Action<string>? OnModeChanged;
    public event Action<string>? OnAction;
    public event Action<string>? OnStatusChanged;

    // Throttle
    private int _pendingSteps;
    private long _lastApplyMs;

    // IsActive guard — MainWindow.UpdateTick checks this to suppress serial polling
    // during active knob rotation so commands don't fight for the serial port.
    private long _lastActivityMs;
    private const int ActiveWindowMs = 500;
    public bool IsActive => Environment.TickCount64 - Interlocked.Read(ref _lastActivityMs) < ActiveWindowMs;

    private readonly Timer _applyTimer;
    private const int ApplyIntervalMs = 50;

    public FlexKnobController(RadioController radio, Config config)
    {
        _radio = radio;
        _config = config;
        // Pre-allocate in a dormant state; Change() activates it on demand
        _applyTimer = new Timer(_ => ApplyPendingSteps(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Connect to FlexKnob - matches Go: serial.Open() + goroutine readLoop</summary>
    public void Connect()
    {
        if (!_config.FlexknobEnabled || string.IsNullOrEmpty(_config.FlexknobPort))
        {
            Logger.Info("FLEXKNOB", "Not enabled or no port (enabled={0}, port=\"{1}\")",
                _config.FlexknobEnabled, _config.FlexknobPort);
            return;
        }

        lock (_lock)
        {
            if (IsConnected)
            {
                Logger.Info("FLEXKNOB", "Already connected");
                return;
            }
        }

        try
        {
            Logger.Info("FLEXKNOB", "Opening {0} at {1} baud...", _config.FlexknobPort, _config.FlexknobBaud);

            // Match Go serial.Mode exactly: 8N1, no flow control
            // IMPORTANT: Do NOT set DtrEnable=true - it causes Arduino-based devices to reset!
            _port = new SerialPort
            {
                PortName      = _config.FlexknobPort,
                BaudRate      = _config.FlexknobBaud,
                DataBits      = 8,
                Parity        = Parity.None,
                StopBits      = StopBits.One,
                Handshake     = Handshake.None,
                ReadTimeout   = 100,
                WriteTimeout  = 500,
                DtrEnable     = false,
                RtsEnable     = false,
                ReadBufferSize = 4096,
                Encoding      = Encoding.ASCII
            };

            _port.Open();
            Thread.Sleep(200);

            lock (_lock) { IsConnected = true; }
            _running = true;

            _readThread = new Thread(ReadLoop)
            {
                Name = "FlexKnob-Reader",
                IsBackground = true
            };
            _readThread.Start();

            Logger.Info("FLEXKNOB", "*** CONNECTED on {0} at {1} baud ***", _config.FlexknobPort, _config.FlexknobBaud);
            OnStatusChanged?.Invoke("Connected");
        }
        catch (Exception ex)
        {
            Logger.Error("FLEXKNOB", "Connect FAILED: {0}", ex.Message);
            Logger.Error("FLEXKNOB", "  Type: {0}", ex.GetType().Name);
            OnStatusChanged?.Invoke("Error: " + ex.Message);
            try { _port?.Close(); } catch { }
            _port = null;
        }
    }

    public void Disconnect()
    {
        _running = false;
        lock (_lock) { IsConnected = false; }

        // Suspend the apply timer
        _applyTimer.Change(Timeout.Infinite, Timeout.Infinite);

        try { _port?.Close(); } catch { }
        try { _port?.Dispose(); } catch { }
        _port = null;

        try { _readThread?.Join(1000); } catch { }
        _readThread = null;

        Logger.Info("FLEXKNOB", "Disconnected");
        OnStatusChanged?.Invoke("Disconnected");
    }

    // ========== READ LOOP ==========

    private void ReadLoop()
    {
        Logger.Info("FLEXKNOB", "Read loop started on {0}", _config.FlexknobPort);

        var buf = new byte[256];
        var lineBuffer = new List<byte>();
        int totalBytesRead = 0;
        int totalCommands = 0;

        try
        {
            while (_running)
            {
                bool connected;
                SerialPort? port;
                lock (_lock) { connected = IsConnected; }
                port = _port;

                if (!connected || port == null || !port.IsOpen)
                {
                    Logger.Info("FLEXKNOB", "Read loop: port not ready (connected={0}, port={1}, open={2})",
                        connected, port != null, port?.IsOpen ?? false);
                    break;
                }

                int avail;
                try { avail = port.BytesToRead; }
                catch { break; }

                if (avail <= 0)
                {
                    Thread.Sleep(5);
                    continue;
                }

                int n;
                try
                {
                    n = port.Read(buf, 0, Math.Min(buf.Length, avail));
                }
                catch (OperationCanceledException) { break; }
                catch (IOException)
                {
                    if (_running) Logger.Warn("FLEXKNOB", "IO error (port closed?)");
                    break;
                }
                catch (Exception ex)
                {
                    if (_running) Logger.Warn("FLEXKNOB", "Read error: {0} ({1})", ex.Message, ex.GetType().Name);
                    break;
                }

                if (n == 0) continue;

                totalBytesRead += n;

                var rawStr = Encoding.ASCII.GetString(buf, 0, n);
                var hexStr = BitConverter.ToString(buf, 0, n).Replace("-", " ");
                Logger.Debug("FLEXKNOB", ">>> Raw ({0} bytes, total={1}): hex=[{2}] str=\"{3}\"",
                    n, totalBytesRead, hexStr, rawStr.Replace("\r", "\\r").Replace("\n", "\\n"));

                for (int i = 0; i < n; i++) lineBuffer.Add(buf[i]);

                while (true)
                {
                    int idx = -1;
                    for (int i = 0; i < lineBuffer.Count; i++)
                    {
                        byte b = lineBuffer[i];
                        if (b == ';' || b == '\n' || b == '\r') { idx = i; break; }
                    }

                    if (idx == -1)
                    {
                        if (lineBuffer.Count > 50)
                        {
                            var overflow = Encoding.ASCII.GetString(lineBuffer.ToArray()).Trim();
                            Logger.Info("FLEXKNOB", "Buffer overflow ({0} chars), processing: \"{1}\"",
                                lineBuffer.Count, overflow);
                            lineBuffer.Clear();

                            if (overflow.Contains(';'))
                            {
                                foreach (var part in overflow.Split(';', StringSplitOptions.RemoveEmptyEntries))
                                {
                                    var cmd = part.Trim();
                                    if (!string.IsNullOrEmpty(cmd)) { totalCommands++; ProcessCommand(cmd, totalCommands); }
                                }
                            }
                            else if (!string.IsNullOrEmpty(overflow))
                            {
                                totalCommands++;
                                ProcessCommand(overflow, totalCommands);
                            }
                        }
                        break;
                    }

                    var cmdBytes = lineBuffer.GetRange(0, idx).ToArray();
                    lineBuffer.RemoveRange(0, idx + 1);

                    var command = Encoding.ASCII.GetString(cmdBytes).Trim();
                    if (!string.IsNullOrEmpty(command)) { totalCommands++; ProcessCommand(command, totalCommands); }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("FLEXKNOB", "Read loop CRASH: {0}", ex.Message);
            Logger.Error("FLEXKNOB", "  Stack: {0}", ex.StackTrace ?? "");
        }

        Logger.Info("FLEXKNOB", "Read loop exited (total: {0} bytes, {1} commands)", totalBytesRead, totalCommands);

        lock (_lock)
        {
            if (IsConnected)
            {
                IsConnected = false;
                OnStatusChanged?.Invoke("Disconnected (port lost)");
            }
        }
    }

    // ========== COMMAND PARSING ==========

    private void ProcessCommand(string input, int cmdNum)
    {
        input = input.Trim().ToUpper();
        if (string.IsNullOrEmpty(input)) return;

        if (cmdNum <= 10)
            Logger.Info("FLEXKNOB", "CMD #{0}: \"{1}\"", cmdNum, input);
        else
            Logger.Debug("FLEXKNOB", "CMD: \"{0}\"", input);

        if (input[0] == 'U' && (input.Length == 1 || char.IsDigit(input[1])))
        {
            int steps = 1;
            if (input.Length > 1 && int.TryParse(input[1..], out var s) && s > 0) steps = s;
            HandleRotation(steps); return;
        }

        if (input[0] == 'D' && (input.Length == 1 || char.IsDigit(input[1])))
        {
            int steps = 1;
            if (input.Length > 1 && int.TryParse(input[1..], out var s) && s > 0) steps = s;
            HandleRotation(-steps); return;
        }

        if (input[0] == 'X' && input.Length >= 3 && char.IsDigit(input[1]))
        {
            int btn = input[1] - '0';
            bool longPress = input[2] == 'L';
            if (btn >= 1) { HandleButton(btn, longPress); return; }
        }

        if (input == "L") { HandleRotation(-1); return; }
        if (input == "R") { HandleRotation(1);  return; }

        if (input.StartsWith("E+") || input.StartsWith("+")) { HandleRotation(ParseSteps(input));  return; }
        if (input.StartsWith("E-") || input.StartsWith("-")) { HandleRotation(-ParseSteps(input)); return; }

        if (input == "CW")  { HandleRotation(1);  return; }
        if (input == "CCW") { HandleRotation(-1); return; }

        if (input.StartsWith("BTN") || (input.StartsWith("B") && input.Length >= 2 && char.IsDigit(input[1])))
            { HandleButton(ParseButtonNumber(input), false); return; }

        if (input is "P" or "PRESS" or "PUSH" or "BUTTON")
            { HandleButton(1, false); return; }

        if (int.TryParse(input, out var rawSteps) && rawSteps != 0)
            { HandleRotation(rawSteps); return; }

        Logger.Info("FLEXKNOB", "Unknown command: \"{0}\"", input);
    }

    private static int ParseSteps(string input)
    {
        var numStr = input.TrimStart('E', 'e', '+', '-');
        return int.TryParse(numStr, out var s) && s > 0 ? s : 1;
    }

    private static int ParseButtonNumber(string input)
    {
        var numStr = input;
        if (numStr.StartsWith("BTN")) numStr = numStr[3..];
        else if (numStr.StartsWith("B")) numStr = numStr[1..];
        return int.TryParse(numStr, out var b) && b > 0 ? b : 1;
    }

    // ========== ROTATION ==========

    private void HandleRotation(int steps)
    {
        if (steps == 0) return;
        Interlocked.Exchange(ref _lastActivityMs, Environment.TickCount64);

        switch (Mode)
        {
            case KnobMode.Frequency: HandleFrequencyRotation(steps); break;
            case KnobMode.Volume:    HandleVolumeRotation(steps);    break;
            case KnobMode.RIT:       HandleRITRotation(steps);       break;
            case KnobMode.Custom:    break;
        }

        var dir = steps > 0 ? "CW" : "CCW";
        OnAction?.Invoke($"{ModeName} {dir} {Math.Abs(steps)}");
    }

    private void HandleFrequencyRotation(int steps)
    {
        if (!_radio.Connected) return;
        Interlocked.Add(ref _pendingSteps, steps);
        ScheduleApply();
    }

    private void ScheduleApply()
    {
        var nowMs = Environment.TickCount64;
        if (nowMs - Interlocked.Read(ref _lastApplyMs) >= ApplyIntervalMs)
        {
            ApplyPendingSteps();
        }
        else
        {
            _applyTimer.Change(ApplyIntervalMs, Timeout.Infinite);
        }
    }

    private void ApplyPendingSteps()
    {
        Interlocked.Exchange(ref _lastApplyMs, Environment.TickCount64);

        var steps = Interlocked.Exchange(ref _pendingSteps, 0);
        if (steps != 0)
        {
            var delta = (long)steps * StepHz;
            _radio.StepFreq(delta);
            Logger.Debug("FLEXKNOB", "Freq: {0:+#;-#}Hz ({1} steps x {2}Hz)", delta, steps, StepHz);
        }

        if (_volumeDirty)
        {
            _volumeDirty = false;
            _radio.SetAFGain(_pendingVolume);
            Logger.Debug("FLEXKNOB", "Vol: {0}", _pendingVolume);
        }

        if (_ritDirty)
        {
            _ritDirty = false;
            _radio.SetRITOffset(_cachedRITOffset);
            Logger.Debug("FLEXKNOB", "RIT: {0}", _cachedRITOffset);
        }
    }

    private int _cachedVolume = -1;
    private int _pendingVolume;
    private bool _volumeDirty;
    private int _cachedRITOffset;
    private bool _cachedRITOn;
    private bool _ritDirty;

    private void HandleVolumeRotation(int steps)
    {
        if (!_radio.Connected) return;
        if (_cachedVolume < 0) _cachedVolume = _radio.GetAFGain();
        _cachedVolume = Math.Clamp(_cachedVolume + steps * 13, 0, 255);
        _pendingVolume = _cachedVolume;
        _volumeDirty = true;
        ScheduleApply();
    }

    private void HandleRITRotation(int steps)
    {
        if (!_radio.Connected) return;
        if (!_cachedRITOn) { _radio.SetRIT(true); _cachedRITOn = true; }
        _cachedRITOffset += steps * 10;
        _ritDirty = true;
        ScheduleApply();
    }

    // ========== BUTTONS ==========

    private void HandleButton(int button, bool longPress)
    {
        Logger.Info("FLEXKNOB", "Button {0} {1}", button, longPress ? "LONG" : "short");

        if (longPress)
        {
            switch (button)
            {
                case 1: if (_radio.Connected) { _radio.SwapVFO(); OnAction?.Invoke("VFO SWAP"); } break;
                case 2: SetStep(100); OnAction?.Invoke("STEP RST"); OnModeChanged?.Invoke(ModeName); break;
                case 3: if (_radio.Connected) { var tx = _radio.GetTXStatus(); _radio.SetPTT(!tx); OnAction?.Invoke(tx ? "PTT OFF" : "PTT ON"); } break;
            }
        }
        else
        {
            switch (button)
            {
                case 1: CycleMode(); break;
                case 2:
                    if (Mode == KnobMode.Frequency) CycleStep();
                    else if (Mode == KnobMode.RIT) { _radio.ClearRIT(); OnAction?.Invoke("RIT CLR"); }
                    break;
                case 3: if (_radio.Connected) { _radio.SwapVFO(); OnAction?.Invoke("VFO SWAP"); } break;
                case 4: if (_radio.Connected) { var tx = _radio.GetTXStatus(); _radio.SetPTT(!tx); OnAction?.Invoke(tx ? "PTT OFF" : "PTT ON"); } break;
            }
        }
    }

    // ========== MODE & STEP ==========

    public void CycleMode()
    {
        Mode = (KnobMode)(((int)Mode + 1) % 4);
        ResetCaches();
        Logger.Info("FLEXKNOB", "Mode: {0}", ModeName);
        OnModeChanged?.Invoke(ModeName);
    }

    public void SetMode(KnobMode mode)
    {
        Mode = mode;
        ResetCaches();
        Logger.Info("FLEXKNOB", "Mode set: {0}", ModeName);
        OnModeChanged?.Invoke(ModeName);
    }

    private void ResetCaches()
    {
        _cachedVolume = -1;
        _volumeDirty = false;
        _cachedRITOffset = 0;
        _cachedRITOn = false;
        _ritDirty = false;
    }

    public void CycleStep()
    {
        _stepIndex = (_stepIndex + 1) % StepSizes.Length;
        Logger.Info("FLEXKNOB", "Step: {0} Hz", StepHz);
        OnAction?.Invoke($"STEP {StepDisplay}");
        OnModeChanged?.Invoke(ModeName);
    }

    public void SetStep(int hz)
    {
        var idx = Array.IndexOf(StepSizes, hz);
        if (idx >= 0)
        {
            _stepIndex = idx;
        }
        else
        {
            // CLEANUP FIX: Log unrecognized step values so misconfigured defaults are visible
            // rather than silently being ignored. Valid values: 10, 50, 100, 500, 1000, 5000, 10000
            Logger.Warn("FLEXKNOB", "SetStep({0}) — not a valid step size, ignoring. Valid: {1}",
                hz, string.Join(", ", StepSizes));
        }
    }

    // ========== STATUS ==========

    public Dictionary<string, object> Status() => new()
    {
        ["connected"] = IsConnected,
        ["mode"]      = ModeName,
        ["step_size"] = StepHz,
        ["port"]      = _config.FlexknobPort
    };

    public void Dispose()
    {
        _applyTimer.Dispose();
        Disconnect();
        GC.SuppressFinalize(this);
    }
}
