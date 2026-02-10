using System;
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
    private Timer? _applyTimer;
    private const int ApplyIntervalMs = 15; // Fast response for knob feel

    public FlexKnobController(RadioController radio, Config config)
    {
        _radio = radio;
        _config = config;
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
                PortName = _config.FlexknobPort,
                BaudRate = _config.FlexknobBaud,
                DataBits = 8,
                Parity = Parity.None,
                StopBits = StopBits.One,
                Handshake = Handshake.None,
                ReadTimeout = 100,     // Match Go: port.SetReadTimeout(100ms)
                WriteTimeout = 500,
                DtrEnable = false,     // Don't toggle DTR (avoids Arduino reset)
                RtsEnable = false,     // Don't toggle RTS
                ReadBufferSize = 4096,
                Encoding = Encoding.ASCII
            };

            _port.Open();

            // Small delay to let device settle (like Go's goroutine scheduling delay)
            Thread.Sleep(200);

            lock (_lock) { IsConnected = true; }
            _running = true;

            // Start read thread (matches Go: go f.readLoop())
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
            // Clean up on failure
            try { _port?.Close(); } catch { }
            _port = null;
        }
    }

    /// <summary>Disconnect from FlexKnob</summary>
    public void Disconnect()
    {
        _running = false;
        lock (_lock) { IsConnected = false; }

        // Close port (will cause Read() to throw, breaking the read loop)
        try { _port?.Close(); } catch { }
        try { _port?.Dispose(); } catch { }
        _port = null;

        // Wait for read thread to finish
        try { _readThread?.Join(1000); } catch { }
        _readThread = null;

        Logger.Info("FLEXKNOB", "Disconnected");
        OnStatusChanged?.Invoke("Disconnected");
    }

    // ========== READ LOOP ==========

    /// <summary>
    /// Polling read loop on dedicated thread.
    /// Checks BytesToRead before calling Read() to avoid costly TimeoutExceptions.
    /// </summary>
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
                // Check connection state
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

                // Check if data is available BEFORE reading (avoids TimeoutException)
                int avail;
                try { avail = port.BytesToRead; }
                catch { break; } // port gone

                if (avail <= 0)
                {
                    // No data — sleep briefly and check again
                    Thread.Sleep(5);
                    continue;
                }

                // Read available bytes
                int n;
                try
                {
                    n = port.Read(buf, 0, Math.Min(buf.Length, avail));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    if (_running)
                        Logger.Warn("FLEXKNOB", "IO error (port closed?)");
                    break;
                }
                catch (Exception ex)
                {
                    if (_running)
                        Logger.Warn("FLEXKNOB", "Read error: {0} ({1})", ex.Message, ex.GetType().Name);
                    break;
                }

                if (n == 0) continue;

                totalBytesRead += n;

                // Log raw data at Debug level (verbose - only needed for troubleshooting)
                var rawStr = Encoding.ASCII.GetString(buf, 0, n);
                var hexStr = BitConverter.ToString(buf, 0, n).Replace("-", " ");
                Logger.Debug("FLEXKNOB", ">>> Raw ({0} bytes, total={1}): hex=[{2}] str=\"{3}\"",
                    n, totalBytesRead, hexStr, rawStr.Replace("\r", "\\r").Replace("\n", "\\n"));

                // Accumulate bytes (matches Go: lineBuffer = append(lineBuffer, buf[:n]...))
                for (int i = 0; i < n; i++)
                    lineBuffer.Add(buf[i]);

                // Process complete commands - split on semicolons, newlines, carriage returns
                while (true)
                {
                    int idx = -1;
                    for (int i = 0; i < lineBuffer.Count; i++)
                    {
                        byte b = lineBuffer[i];
                        if (b == ';' || b == '\n' || b == '\r')
                        {
                            idx = i;
                            break;
                        }
                    }

                    if (idx == -1)
                    {
                        // No delimiter found - overflow handling (matches Go: len(lineBuffer) > 50)
                        if (lineBuffer.Count > 50)
                        {
                            var overflow = Encoding.ASCII.GetString(lineBuffer.ToArray()).Trim();
                            Logger.Info("FLEXKNOB", "Buffer overflow ({0} chars), processing: \"{1}\"",
                                lineBuffer.Count, overflow);
                            lineBuffer.Clear();

                            // Try splitting on semicolons within the overflow
                            if (overflow.Contains(';'))
                            {
                                foreach (var part in overflow.Split(';', StringSplitOptions.RemoveEmptyEntries))
                                {
                                    var cmd = part.Trim();
                                    if (!string.IsNullOrEmpty(cmd))
                                    {
                                        totalCommands++;
                                        ProcessCommand(cmd, totalCommands);
                                    }
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

                    // Extract command before delimiter
                    var cmdBytes = lineBuffer.GetRange(0, idx).ToArray();
                    lineBuffer.RemoveRange(0, idx + 1); // Remove command + delimiter

                    var command = Encoding.ASCII.GetString(cmdBytes).Trim();
                    if (!string.IsNullOrEmpty(command))
                    {
                        totalCommands++;
                        ProcessCommand(command, totalCommands);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("FLEXKNOB", "Read loop CRASH: {0}", ex.Message);
            Logger.Error("FLEXKNOB", "  Stack: {0}", ex.StackTrace ?? "");
        }

        Logger.Info("FLEXKNOB", "Read loop exited (total: {0} bytes, {1} commands)", totalBytesRead, totalCommands);

        // Mark disconnected if we exit unexpectedly
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

    /// <summary>Parse and dispatch a single command. Logged at Info for first 100, Debug after.</summary>
    private void ProcessCommand(string input, int cmdNum)
    {
        input = input.Trim().ToUpper();
        if (string.IsNullOrEmpty(input)) return;

        // Log at Info level for first 10 commands (diagnostic), then Debug
        if (cmdNum <= 10)
            Logger.Info("FLEXKNOB", "CMD #{0}: \"{1}\"", cmdNum, input);
        else
            Logger.Debug("FLEXKNOB", "CMD: \"{0}\"", input);

        // ===== FlexKnob native protocol =====

        // U = up 1, U02 = up 2, U03 = up 3
        if (input[0] == 'U' && (input.Length == 1 || char.IsDigit(input[1])))
        {
            int steps = 1;
            if (input.Length > 1 && int.TryParse(input[1..], out var s) && s > 0) steps = s;
            HandleRotation(steps);
            return;
        }

        // D = down 1, D02 = down 2, D03 = down 3
        if (input[0] == 'D' && (input.Length == 1 || char.IsDigit(input[1])))
        {
            int steps = 1;
            if (input.Length > 1 && int.TryParse(input[1..], out var s) && s > 0) steps = s;
            HandleRotation(-steps);
            return;
        }

        // X1S/X2S/X3S = button short, X1L/X2L/X3L = button long
        if (input[0] == 'X' && input.Length >= 3 && char.IsDigit(input[1]))
        {
            int btn = input[1] - '0';
            bool longPress = input[2] == 'L';
            if (btn >= 1) { HandleButton(btn, longPress); return; }
        }

        // ===== Legacy protocols (matching Go processInput exactly) =====

        if (input == "L") { HandleRotation(-1); return; }
        if (input == "R") { HandleRotation(1); return; }

        if (input.StartsWith("E+") || input.StartsWith("+"))
        { HandleRotation(ParseSteps(input)); return; }
        if (input.StartsWith("E-") || input.StartsWith("-"))
        { HandleRotation(-ParseSteps(input)); return; }

        if (input == "CW") { HandleRotation(1); return; }
        if (input == "CCW") { HandleRotation(-1); return; }

        // Button: B1, B2, BTN1, BTN2
        if (input.StartsWith("BTN") || (input.StartsWith("B") && input.Length >= 2 && char.IsDigit(input[1])))
        { HandleButton(ParseButtonNumber(input), false); return; }

        if (input is "P" or "PRESS" or "PUSH" or "BUTTON")
        { HandleButton(1, false); return; }

        // Raw number = rotation steps
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
        // Match Go: TrimLeft("BTN") then TrimLeft("B") then TrimLeft("R")
        var numStr = input;
        if (numStr.StartsWith("BTN")) numStr = numStr[3..];
        else if (numStr.StartsWith("B")) numStr = numStr[1..];
        return int.TryParse(numStr, out var b) && b > 0 ? b : 1;
    }

    // ========== ROTATION ==========

    private void HandleRotation(int steps)
    {
        if (steps == 0) return;

        switch (Mode)
        {
            case KnobMode.Frequency:
                HandleFrequencyRotation(steps);
                break;
            case KnobMode.Volume:
                HandleVolumeRotation(steps);
                break;
            case KnobMode.RIT:
                HandleRITRotation(steps);
                break;
            case KnobMode.Custom:
                break;
        }

        var dir = steps > 0 ? "CW" : "CCW";
        OnAction?.Invoke($"{ModeName} {dir} {Math.Abs(steps)}");
    }

    private void HandleFrequencyRotation(int steps)
    {
        if (!_radio.Connected) return;

        Interlocked.Add(ref _pendingSteps, steps);

        var nowMs = Environment.TickCount64;
        if (nowMs - Interlocked.Read(ref _lastApplyMs) >= ApplyIntervalMs)
        {
            // Enough time passed — apply immediately
            ApplyPendingSteps();
        }
        else
        {
            // Schedule a single deferred apply (reuse timer, don't spawn tasks)
            _applyTimer?.Dispose();
            _applyTimer = new Timer(_ => ApplyPendingSteps(), null, ApplyIntervalMs, Timeout.Infinite);
        }
    }

    private void ApplyPendingSteps()
    {
        var steps = Interlocked.Exchange(ref _pendingSteps, 0);
        if (steps == 0) return;

        Interlocked.Exchange(ref _lastApplyMs, Environment.TickCount64);
        var delta = (long)steps * StepHz;
        _radio.StepFreq(delta);
        Logger.Debug("FLEXKNOB", "Freq: {0:+#;-#}Hz ({1} steps x {2}Hz)", delta, steps, StepHz);
    }

    private int _cachedVolume = -1;
    private int _cachedRITOffset;
    private bool _cachedRITOn;

    private void HandleVolumeRotation(int steps)
    {
        if (!_radio.Connected) return;
        if (_cachedVolume < 0) _cachedVolume = _radio.GetAFGain(); // first time only
        _cachedVolume = Math.Clamp(_cachedVolume + steps * 13, 0, 255);
        _radio.SetAFGain(_cachedVolume);
    }

    private void HandleRITRotation(int steps)
    {
        if (!_radio.Connected) return;
        if (!_cachedRITOn) { _radio.SetRIT(true); _cachedRITOn = true; }
        _cachedRITOffset += steps * 10;
        _radio.SetRITOffset(_cachedRITOffset);
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
        _cachedRITOffset = 0;
        _cachedRITOn = false;
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
        if (idx >= 0) _stepIndex = idx;
    }

    // ========== STATUS ==========

    public Dictionary<string, object> Status() => new()
    {
        ["connected"] = IsConnected,
        ["mode"] = ModeName,
        ["step_size"] = StepHz,
        ["port"] = _config.FlexknobPort
    };

    public void Dispose()
    {
        _applyTimer?.Dispose();
        Disconnect();
        GC.SuppressFinalize(this);
    }
}
