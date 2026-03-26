using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;

namespace HamDeck.Services;

/// <summary>
/// CAT command controller for Yaesu FTDX-101MP via serial port.
/// Thread-safe with automatic reconnection.
/// </summary>
public class RadioController : IDisposable
{
    private SerialPort? _port;
    private readonly object _lock = new();
    private int _failCount;

    public bool Connected { get; private set; }
    public string PortName { get; private set; } = "";
    public long LastFrequency { get; private set; }
    public string LastMode { get; private set; } = "";
    public int LastPower { get; private set; }

    /// <summary>Last cached TX/PTT state — updated by GetTXStatus() and proxy traffic.</summary>
    public bool LastTXState { get; private set; }

    // Cached values populated by proxy traffic so the UI can update without serial queries
    public long LastFreqB { get; private set; }
    public bool LastSplit { get; private set; }
    public string LastVFO { get; private set; } = "A";
    public int LastSMeter { get; private set; }
    public int LastAFGain { get; private set; }

    /// <summary>Timestamp of last proxy command — UI uses cached values while proxy is active</summary>
    public long LastProxyActivityMs { get; private set; }

    /// <summary>True when a proxy client has been active within the last 500ms</summary>
    public bool ProxyIsActive => Environment.TickCount64 - LastProxyActivityMs < 500;

    /// <summary>
    /// Timestamp of last successful GetFreq() — set by WPF UpdateTick every 200ms.
    /// ApiServer uses this to serve cached status without serial queries on web polls.
    /// </summary>
    public long LastCacheRefreshMs { get; private set; }

    // ========== CONNECTION ==========

    public void Connect(string portName, int baud)
    {
        Logger.Info("RADIO", "Opening {0} at {1} baud...", portName, baud);

        var port = new SerialPort(portName, baud, Parity.None, 8, StopBits.Two)
        {
            ReadTimeout = 150,
            WriteTimeout = 500,
            DtrEnable = true,
            RtsEnable = true
        };

        try
        {
            port.Open();
            Thread.Sleep(100);

            // BUG FIX: Assign _port inside the lock so Send() (which also acquires _lock
            // before reading _port) cannot observe a partially-initialized port state.
            // Previously _port was assigned outside the lock, creating a small race window.
            lock (_lock)
            {
                _port = port;
                PortName = portName;
                _failCount = 0;
            }

            // Test connection — Send() will now see the locked _port assignment above
            var freq = GetFreq();
            if (freq > 0)
            {
                Connected = true;
                Logger.Info("RADIO", "Connected! Freq: {0} Hz", freq);
            }
            else
            {
                lock (_lock) { _port = null; PortName = ""; }
                port.Close();
                port.Dispose();
                throw new Exception("Radio not responding");
            }
        }
        catch
        {
            try { port.Dispose(); } catch { }
            lock (_lock) { _port = null; }
            throw;
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            Connected = false;
            if (_port != null)
            {
                try
                {
                    if (_port.IsOpen)
                        _port.Close();
                }
                catch { }
                try { _port.Dispose(); } catch { }
                _port = null;

                // Windows needs time to fully release COM port handle
                Thread.Sleep(100);
            }
            PortName = "";
        }
    }

    /// <summary>Auto-detect the radio COM port by trying each available port.</summary>
    public string? AutoDetect(int baud = 38400)
    {
        foreach (var name in SerialPort.GetPortNames())
        {
            try
            {
                Connect(name, baud);
                if (Connected) return name;
            }
            catch { Disconnect(); }
        }
        return null;
    }

    // ========== LOW-LEVEL SEND ==========

    private string Send(string cmd, bool expectResponse = true)
    {
        if (_port == null || !_port.IsOpen) return "";

        lock (_lock)
        {
            try
            {
                _port.DiscardInBuffer();
                _port.Write(cmd);

                if (!expectResponse) return "";

                // Reduced from 30ms → 10ms. The FTDX-101 responds in 5-15ms at 38400 baud.
                // The original 30ms sleep was consuming most of the 200ms UpdateTick budget
                // across 7+ queries per cycle.
                Thread.Sleep(10);
                var buf = new byte[256];
                var response = new List<byte>();
                var deadline = DateTime.UtcNow.AddMilliseconds(200);

                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        int n = _port.Read(buf, 0, buf.Length);
                        if (n == 0) break;
                        for (int i = 0; i < n; i++) response.Add(buf[i]);
                        if (response.Contains((byte)';')) break;
                    }
                    catch (TimeoutException) { break; }
                }

                return System.Text.Encoding.ASCII.GetString(response.ToArray()).Trim();
            }
            catch
            {
                Connected = false;
                return "";
            }
        }
    }


    // Yaesu CAT query opcodes — a query expects a response; everything else is fire-and-forget.
    // Using a HashSet gives O(1) lookup vs the original 50-branch OR chain.
    private static readonly HashSet<string> _queryCommands = new(StringComparer.Ordinal)
    {
        "FA;", "FB;", "IF;",
        "MD0;", "TX;", "PC;",
        "ST;", "FT;", "VS;", "AG0;", "AG1;",
        "RG0;", "SM0;", "SM1;",
        "RM0;", "RM1;", "RM2;", "RM3;",
        "RM4;", "RM5;", "RM6;", "RM7;",
        "RM8;", "RM9;",
        "PA0;", "RA0;", "GT0;",
        "NB0;", "NR0;", "BC0;",
        "RT;",  "XT;",  "RD;",
        "VX;",  "PR;",  "PR0;", "PR1;", "LK;",
        "KS;",  "KP;",  "BI;",
        "AN0;", "AN1;", "SH0;",
        "AC;",  "ID;",  "OI;",
        "FN;",  "FR;",  "FS;",
        "SQ0;", "SQ1;", "MG;",
        "PS;",  "RS;",  "SC;",
        "SD;",  "SY;",  "DA;",
        "MC;",  "MS;",
        "MO;"
    };

    /// <summary>
    /// Public bridge used by TcpCatProxy to route raw CAT commands through the serial lock.
    /// Uses an explicit list of Yaesu query commands so set commands are always fire-and-forget.
    /// Parses responses to update cached properties so the UI can display live data
    /// without competing for the serial lock.
    /// </summary>
    public string SendRaw(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return "";
        cmd = cmd.Trim();
        if (!cmd.EndsWith(";")) cmd += ";";

        // Yaesu query commands — everything else is a set (fire-and-forget)
        // Queries are the bare opcode + semicolon (no payload digits)
        var upper = cmd.ToUpper();
        bool isQuery = _queryCommands.Contains(upper);

        // Mark proxy activity so the UI switches to cached-value mode
        LastProxyActivityMs = Environment.TickCount64;

        string response = Send(cmd, isQuery);

        // Parse response to keep cached properties current for the UI
        if (!string.IsNullOrEmpty(response))
            UpdateCacheFromResponse(response);

        return response;
    }

    /// <summary>
    /// Parse a CAT response and update cached properties.
    /// Called from SendRaw so proxy traffic keeps the UI fed with live data.
    /// </summary>
    private void UpdateCacheFromResponse(string resp)
    {
        try
        {
            if (resp.StartsWith("FA") && resp.Length >= 11 &&
                long.TryParse(resp[2..11], out var freqA))
            {
                LastFrequency = freqA;
                _failCount = 0;
            }
            else if (resp.StartsWith("FB") && resp.Length >= 11 &&
                     long.TryParse(resp[2..11], out var freqB))
            {
                LastFreqB = freqB;
            }
            else if (resp.StartsWith("MD0") && resp.Length >= 4 &&
                     ModeMap.TryGetValue(resp[3], out var mode))
            {
                LastMode = mode;
            }
            else if (resp.StartsWith("TX") && resp.Length >= 3)
            {
                LastTXState = resp[2] != '0';
            }
            else if (resp.StartsWith("ST") && resp.Length >= 3)
            {
                LastSplit = resp[2] == '1';
            }
            else if (resp.StartsWith("FT") && resp.Length >= 3)
            {
                // FT (TX VFO select) — FT1 means TX on sub VFO = split active
                LastSplit = resp[2] != '0';
            }
            else if (resp.StartsWith("VS") && resp.Length >= 3)
            {
                LastVFO = resp[2] == '0' ? "A" : "B";
            }
            else if (resp.StartsWith("SM0") && resp.Length >= 6 &&
                     int.TryParse(resp[3..6], out var smeter))
            {
                LastSMeter = smeter;
            }
            else if (resp.StartsWith("PC") && resp.Length >= 5 &&
                     int.TryParse(resp[2..5], out var pwr))
            {
                LastPower = pwr;
            }
            else if (resp.StartsWith("AG0") && resp.Length >= 6 &&
                     int.TryParse(resp[3..6], out var afg))
            {
                LastAFGain = afg;
            }
            else if (resp.StartsWith("IF") && resp.Length >= 27)
            {
                // IF response: IF[MemCh 3][Freq 9][+/-][Offset 4][RIT][XIT][x][x][TX][Mode][VFO]...
                if (long.TryParse(resp[5..14], out var ifFreq) && ifFreq > 0)
                    LastFrequency = ifFreq;
                // Mode at position 21 (0-indexed from 'I')
                if (ModeMap.TryGetValue(resp[21], out var ifMode))
                    LastMode = ifMode;
            }
        }
        catch
        {
            // Parsing best-effort — never let a malformed response crash anything
        }
    }

    // ========== FREQUENCY ==========

    public long GetFreq()
    {
        var resp = Send("FA;");
        if (resp.StartsWith("FA") && resp.Length >= 11 &&
            long.TryParse(resp[2..11], out var freq))
        {
            _failCount = 0;
            LastFrequency = freq;
            LastCacheRefreshMs = Environment.TickCount64;
            if (!Connected) { Connected = true; Logger.Info("RADIO", "Reconnected"); }
            return freq;
        }
        _failCount++;
        if (_failCount >= 5 && Connected) { Connected = false; Logger.Warn("RADIO", "Connection lost"); }
        return 0;
    }

    public long GetFreqB()
    {
        var resp = Send("FB;");
        if (resp.StartsWith("FB") && resp.Length >= 11 &&
            long.TryParse(resp[2..11], out var freq))
            return freq;
        return 0;
    }

    public void SetFreq(long hz)
    {
        hz = Math.Clamp(hz, 30_000, 75_000_000);
        Send($"FA{hz:D9};", false);
    }

    /// <summary>Step frequency using cached value — no radio query, fire-and-forget for knob speed.
    /// Snaps to the nearest step boundary in the turn direction so the frequency stays clean.</summary>
    public void StepFreq(long stepHz)
    {
        var f = LastFrequency;
        if (f <= 0) f = GetFreq();
        if (f <= 0) return;
        var absStep = Math.Abs(stepHz);
        long newFreq;
        if (stepHz > 0)
            newFreq = ((f / absStep) + 1) * absStep;   // snap up to next boundary
        else
            newFreq = ((f - 1) / absStep) * absStep;   // snap down to prev boundary
        LastFrequency = newFreq;
        SetFreq(newFreq);
    }

    // ========== MODE ==========

    private static readonly Dictionary<char, string> ModeMap = new()
    {
        ['1'] = "LSB", ['2'] = "USB", ['3'] = "CW-U", ['4'] = "FM",
        ['5'] = "AM",  ['6'] = "RTTY-L", ['7'] = "CW-L", ['8'] = "DATA-L",
        ['9'] = "RTTY-U", ['A'] = "DATA-FM", ['B'] = "FM-N", ['C'] = "DATA-U",
        ['D'] = "AM-N", ['E'] = "C4FM"
    };

    private static readonly Dictionary<string, string> ModeCode = new()
    {
        ["LSB"] = "1", ["USB"] = "2", ["CW-U"] = "3", ["CW"] = "3", ["FM"] = "4",
        ["AM"] = "5", ["RTTY-L"] = "6", ["CW-L"] = "7", ["DATA-L"] = "8",
        ["RTTY-U"] = "9", ["DATA-FM"] = "A", ["FM-N"] = "B", ["DATA-U"] = "C",
        ["AM-N"] = "D", ["C4FM"] = "E"
    };

    public string GetMode()
    {
        var resp = Send("MD0;");
        if (resp.StartsWith("MD0") && resp.Length >= 4 && ModeMap.TryGetValue(resp[3], out var mode))
        {
            LastMode = mode;
            return mode;
        }
        return "?";
    }

    public void SetMode(string mode)
    {
        if (ModeCode.TryGetValue(mode.ToUpper(), out var code))
            Send($"MD0{code};", false);
    }

    // ========== VFO ==========

    public string GetVFO()
    {
        var resp = Send("VS;");
        if (resp.StartsWith("VS") && resp.Length >= 3)
            return resp[2] == '0' ? "A" : "B";
        return "?";
    }

    public void SetVFO(string vfo) => Send(vfo == "A" ? "VS0;" : "VS1;", false);
    public void SwapVFO() => Send("SV;", false);

    public void CopyVFO(string from, string to)
    {
        var cur = GetVFO();
        SetVFO(from); var freq = GetFreq();
        SetVFO(to); SetFreq(freq);
        SetVFO(cur);
    }

    public void QuickSplit(long offsetHz)
    {
        var freq = GetFreq();
        SetVFO("B"); SetFreq(freq + offsetHz);
        SetVFO("A"); SetSplit(true);
    }

    // ========== TX ==========

    public bool GetTXStatus()
    {
        var resp = Send("TX;");
        var state = resp.StartsWith("TX") && resp.Length >= 3 && resp[2] != '0';
        LastTXState = state;
        return state;
    }

    public void SetPTT(bool on) => Send(on ? "TX1;" : "TX0;", false);

    // ========== POWER ==========

    public int GetPower()
    {
        var resp = Send("PC;");
        if (resp.StartsWith("PC") && resp.Length >= 5 && int.TryParse(resp[2..5], out var pwr))
        {
            LastPower = pwr;
            return pwr;
        }
        return LastPower; // return cached on failure rather than 0
    }

    public void SetPower(int watts)
    {
        watts = Math.Clamp(watts, 5, 200);
        Send($"PC{watts:D3};", false);
    }

    // ========== METERS ==========

    public int GetSMeter()
    {
        var resp = Send("SM0;");
        if (resp.StartsWith("SM0") && resp.Length >= 6 && int.TryParse(resp[3..6], out var v)) return v;
        return 0;
    }

    public int GetPowerMeter()
    {
        var resp = Send("RM5;");
        if (resp.StartsWith("RM5") && resp.Length >= 6 && int.TryParse(resp[3..6], out var v)) return v;
        return 0;
    }

    public int GetSWR()
    {
        var resp = Send("RM6;");
        if (resp.StartsWith("RM6") && resp.Length >= 6 && int.TryParse(resp[3..6], out var v)) return v;
        return 0;
    }

    public int GetALC()
    {
        var resp = Send("RM4;");
        if (resp.StartsWith("RM4") && resp.Length >= 6 && int.TryParse(resp[3..6], out var v)) return v;
        return 0;
    }

    // ========== SPLIT ==========

    public bool GetSplit()
    {
        var resp = Send("ST;");
        return resp.StartsWith("ST") && resp.Length >= 3 && resp[2] == '1';
    }

    public void SetSplit(bool on) => Send(on ? "ST1;" : "ST0;", false);

    // ========== AF/RF GAIN ==========

    public int GetAFGain()
    {
        var resp = Send("AG0;");
        if (resp.StartsWith("AG0") && resp.Length >= 6 && int.TryParse(resp[3..6], out var v)) return v;
        return 0;
    }

    public void SetAFGain(int level) => Send($"AG0{Math.Clamp(level, 0, 255):D3};", false);

    // ========== SUB-BAND AF GAIN ==========

    public int GetSubAFGain()
    {
        var resp = Send("AG1;");
        if (resp.StartsWith("AG1") && resp.Length >= 6 && int.TryParse(resp[3..6], out var v)) return v;
        return 0;
    }

    public void SetSubAFGain(int level) => Send($"AG1{Math.Clamp(level, 0, 255):D3};", false);

    public int GetRFGain()
    {
        var resp = Send("RG0;");
        if (resp.StartsWith("RG0") && resp.Length >= 6 && int.TryParse(resp[3..6], out var v)) return v;
        return 0;
    }

    public void SetRFGain(int level) => Send($"RG0{Math.Clamp(level, 0, 255):D3};", false);
    public void SetMicGain(int level) => Send($"MG{Math.Clamp(level, 0, 100):D3};", false);

    // ========== FILTERS ==========

    public bool GetNB() => Send("NB0;").Contains("NB01");
    public void SetNB(bool on) => Send(on ? "NB01;" : "NB00;", false);

    public bool GetNR() => Send("NR0;").Contains("NR01");
    public void SetNR(bool on) => Send(on ? "NR01;" : "NR00;", false);

    public bool GetNotch() => Send("BC0;").Contains("BC01");
    public void SetNotch(bool on) => Send(on ? "BC01;" : "BC00;", false);

    public bool GetLock() => Send("LK;").Contains("LK1");
    public void SetLock(bool on) => Send(on ? "LK1;" : "LK0;", false);

    // ========== PREAMP / ATT ==========

    public int GetPreamp()
    {
        var resp = Send("PA0;");
        if (resp.StartsWith("PA0") && resp.Length >= 4 && int.TryParse(resp[3].ToString(), out var v)) return v;
        return 0;
    }

    public void SetPreamp(bool on) => Send(on ? "PA01;" : "PA00;", false);
    public void CyclePreamp() => Send($"PA0{(GetPreamp() + 1) % 3};", false);

    public bool GetATT() => !Send("RA0;").Contains("RA000");
    public void SetATT(bool on) => Send(on ? "RA01;" : "RA00;", false);

    // ========== AGC ==========

    public string GetAGC()
    {
        var resp = Send("GT0;");
        if (resp.StartsWith("GT0") && resp.Length >= 4)
            return resp[3] switch { '0' => "OFF", '1' => "FAST", '2' => "MID", '3' => "SLOW", '4' => "AUTO", '5' => "AUTO", '6' => "AUTO", _ => "?" };
        return "?";
    }

    public void SetAGC(string mode)
    {
        var codes = new Dictionary<string, string>
            { ["OFF"] = "0", ["FAST"] = "1", ["MID"] = "2", ["SLOW"] = "3", ["AUTO"] = "4" };
        if (codes.TryGetValue(mode.ToUpper(), out var c)) Send($"GT0{c};", false);
    }

    // ========== VOX / COMP ==========

    public bool GetVOX() => Send("VX;").Contains("VX1");
    public void SetVOX(bool on) => Send(on ? "VX1;" : "VX0;", false);

    public bool GetComp()
    {
        var resp = Send("PR0;");
        // Answer: PR0P2; where P2: 1=OFF, 2=ON
        return resp.StartsWith("PR0") && resp.Length >= 4 && resp[3] == '2';
    }
    public void SetComp(bool on) => Send(on ? "PR02;" : "PR01;", false);

    // ========== MONITOR ==========
    // CAT manual: ML P1 P2 P2 P2 ;
    //   P1=0: ON/OFF — P2: 000=OFF, 001=ON
    //   P1=1: Level  — P2: 000-100
    // Read: ML0; → ML0PPP;   ML1; → ML1PPP;

    private int _savedMonLevel = 50;

    /// <summary>Get monitor state via ML0; query</summary>
    public bool GetMon()
    {
        var resp = Send("ML0;");
        // Response: ML0PPP; — 000=off, 001=on
        if (resp.StartsWith("ML0") && resp.Length >= 6 &&
            int.TryParse(resp[3..6], out var state))
            return state > 0;
        return false;
    }

    /// <summary>Set monitor on or off — off kills TX audio feedback loop when remote</summary>
    public void SetMon(bool on)
    {
        if (!on)
        {
            // Save current level before turning off so we can restore it
            var resp = Send("ML1;");
            if (resp.StartsWith("ML1") && resp.Length >= 6 &&
                int.TryParse(resp[3..6], out var level) && level > 0)
                _savedMonLevel = level;
            Send("ML0000;", false);               // P1=0, P2=000 → MONI OFF
        }
        else
        {
            Send("ML0001;", false);               // P1=0, P2=001 → MONI ON
            Send($"ML1{_savedMonLevel:D3};", false); // P1=1 → restore saved level
        }
    }

    // ========== RIT / XIT ==========

    public (bool On, int Offset) GetRIT()
    {
        bool on = Send("RT;").Contains("RT1");
        int offset = 0;
        var resp2 = Send("RD;");
        if (resp2.StartsWith("RD") && resp2.Length >= 7) int.TryParse(resp2[2..7], out offset);
        return (on, offset);
    }

    public void SetRIT(bool on) => Send(on ? "RT1;" : "RT0;", false);
    public void SetRITOffset(int hz) => Send(hz >= 0 ? $"RU{hz:D4};" : $"RD{-hz:D4};", false);
    public void ClearRIT() => Send("RC;", false);

    public bool GetXIT() => Send("XT;").Contains("XT1");
    public void SetXIT(bool on) => Send(on ? "XT1;" : "XT0;", false);

    // ========== CW ==========

    public int GetCWSpeed()
    {
        var resp = Send("KS;");
        if (resp.StartsWith("KS") && resp.Length >= 5 && int.TryParse(resp[2..5], out var v)) return v;
        return 0;
    }

    public void SetCWSpeed(int wpm) => Send($"KS{Math.Clamp(wpm, 4, 60):D3};", false);
    public void PlayCWMemory(int mem) { if (mem >= 1 && mem <= 5) Send($"KY{mem};", false); }
    public void StopCWMemory() => Send("KY0;", false);
    public void SendCWText(string text) => Send($"KM1{text[..Math.Min(text.Length, 50)]};", false);

    public int GetCWPitch()
    {
        var resp = Send("KP;");
        if (resp.StartsWith("KP") && resp.Length >= 4 && int.TryParse(resp[2..4], out var step))
            return 300 + step * 10;  // PDF: 00=300Hz to 75=1050Hz, 10Hz steps
        return 600;
    }

    public void SetCWPitch(int hz) => Send($"KP{(Math.Clamp(hz, 300, 1050) - 300) / 10:D2};", false);
    public int GetBreakIn() { var r = Send("BI;"); return r.StartsWith("BI") && r.Length >= 3 ? r[2] - '0' : 1; }
    public void SetBreakIn(int mode) => Send($"BI{Math.Clamp(mode, 0, 1)};", false);

    /// <summary>
    /// Returns true if the radio CW memory playback is active.
    /// KY; query: FTDX-101 responds KY1; during playback, KY0; when idle.
    /// NOTE 18 FIX: Was a hard-coded stub always returning false.
    /// </summary>
    public bool GetCWMemoryStatus()
    {
        var resp = Send("KY;");
        return resp.StartsWith("KY") && resp.Length >= 3 && resp[2] == '1';
    }

    // ========== ANTENNA / TUNER / MISC ==========

    public void StartTune() => Send("AC002;", false);
    public void RecallMemory(int num) => Send($"MC{num:D3};", false);
    public void SetWidth(int w) => Send($"SH00{w:D2};", false);

    public int GetAntenna()
    {
        var resp = Send("AN0;");
        if (resp.StartsWith("AN0") && resp.Length >= 4 && int.TryParse(resp[3].ToString(), out var v)) return v;
        return 1;
    }

    public void SetAntenna(int ant) => Send($"AN0{Math.Clamp(ant, 1, 3)};", false);
    public void ToggleAntenna() => SetAntenna(GetAntenna() == 1 ? 2 : 1);

    public bool GetRxAntenna()
    {
        // ANT3 SELECT is a menu item: OPERATION SETTING -> GENERAL -> ANT3 SELECT
        // EX command: P1=03 (OPERATION SETTING), P2=01 (GENERAL), P3=03 (ANT3 SELECT)
        // Answer: EX030103P4; where P4=0 means TRX, P4=1 means RX ANT
        var resp = Send("EX030103;");
        return resp.Length >= 9 && resp[8] == '1';
    }

    public void SetRxAntenna(bool useRxAnt) => Send($"EX030103{(useRxAnt ? 1 : 0)};", false);

    // ========== REMOTE TX AUDIO (USB CODEC) ==========
    // CAT Reference: EX command, Table 2
    // P1=01 (RADIO SETTING), P2=01 (MODE SSB)
    //   P3=11: SSB MOD SOURCE — 0: MIC, 1: REAR
    //   P3=12: REAR SELECT   — 0: DATA, 1: USB
    //   P3=13: RPORT GAIN    — 000-100

    private int _savedRPortGain = -1;

    /// <summary>Get SSB modulation source: true = REAR (USB/DATA), false = MIC</summary>
    public bool GetSSBModSourceRear()
    {
        var resp = Send("EX010111;");
        return resp.Length >= 9 && resp[8] == '1';
    }

    /// <summary>Set SSB modulation source: true = REAR, false = MIC</summary>
    public void SetSSBModSource(bool rear) => Send($"EX010111{(rear ? 1 : 0)};", false);

    /// <summary>Get REAR port select: true = USB audio codec, false = DATA jack</summary>
    public bool GetRearSelectUSB()
    {
        var resp = Send("EX010112;");
        return resp.Length >= 9 && resp[8] == '1';
    }

    /// <summary>Set REAR port select: true = USB, false = DATA</summary>
    public void SetRearSelect(bool usb) => Send($"EX010112{(usb ? 1 : 0)};", false);

    /// <summary>Get RPORT gain (0-100)</summary>
    public int GetRPortGain()
    {
        var resp = Send("EX010113;");
        if (resp.StartsWith("EX010113") && resp.Length >= 11 &&
            int.TryParse(resp[8..11], out var gain))
            return gain;
        return 50;
    }

    /// <summary>Set RPORT gain (0-100)</summary>
    public void SetRPortGain(int gain) => Send($"EX010113{Math.Clamp(gain, 0, 100):D3};", false);

    /// <summary>
    /// Enable remote TX mode: sets SSB MOD SOURCE to REAR, REAR SELECT to USB,
    /// and RPORT GAIN to 50 for browser mic levels.
    /// Saves original RPORT GAIN for restore on disable.
    /// </summary>
    public void EnableRemoteTx()
    {
        _savedRPortGain = GetRPortGain();
        SetSSBModSource(true);   // MOD SOURCE → REAR
        Thread.Sleep(50);
        SetRearSelect(true);     // REAR SELECT → USB
        Thread.Sleep(50);
        SetRPortGain(50);        // Set gain for browser mic
        Logger.Info("RADIO", "Remote TX enabled (MOD=REAR, REAR=USB, RPORT GAIN=50, saved={0})", _savedRPortGain);
    }

    /// <summary>
    /// Disable remote TX mode: sets SSB MOD SOURCE back to MIC and
    /// restores original RPORT GAIN.
    /// </summary>
    public void DisableRemoteTx()
    {
        SetSSBModSource(false);  // MOD SOURCE → MIC
        if (_savedRPortGain >= 0)
        {
            Thread.Sleep(50);
            SetRPortGain(_savedRPortGain);
            Logger.Info("RADIO", "Remote TX disabled (MOD=MIC, RPORT GAIN restored to {0})", _savedRPortGain);
            _savedRPortGain = -1;
        }
        else
        {
            Logger.Info("RADIO", "Remote TX disabled (MOD=MIC)");
        }
    }

    // ========== SSB OUT LEVEL (RX audio volume to USB) ==========
    // EX command: P1=01 (RADIO SETTING), P2=01 (MODE SSB), P3=09 (SSB OUT LEVEL)
    // P4 = 000-100

    /// <summary>Get SSB OUT LEVEL (0-100) — controls audio volume sent to USB codec</summary>
    public int GetSSBOutLevel()
    {
        var resp = Send("EX010109;");
        if (resp.StartsWith("EX010109") && resp.Length >= 11 &&
            int.TryParse(resp[8..11], out var level))
            return level;
        return 50;
    }

    /// <summary>Set SSB OUT LEVEL (0-100)</summary>
    public void SetSSBOutLevel(int level) => Send($"EX010109{Math.Clamp(level, 0, 100):D3};", false);

    // ========== PORT DETECTION ==========

    public static string[] GetAvailablePorts() => SerialPort.GetPortNames();

    public void Dispose()
    {
        Disconnect();
        GC.SuppressFinalize(this);
    }
}
