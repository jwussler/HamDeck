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

            _port = port;
            PortName = portName;
            _failCount = 0;

            // Test connection
            var freq = GetFreq();
            if (freq > 0)
            {
                Connected = true;
                Logger.Info("RADIO", "Connected! Freq: {0} Hz", freq);
            }
            else
            {
                port.Close();
                port.Dispose();
                _port = null;
                throw new Exception("Radio not responding");
            }
        }
        catch
        {
            // Clean up port on any failure
            try { port.Dispose(); } catch { }
            _port = null;
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

                Thread.Sleep(30);
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

    // ========== FREQUENCY ==========

    public long GetFreq()
    {
        var resp = Send("FA;");
        if (resp.StartsWith("FA") && resp.Length >= 11 &&
            long.TryParse(resp[2..11], out var freq))
        {
            _failCount = 0;
            LastFrequency = freq;
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

    /// <summary>Step frequency using cached value — no radio query, fire-and-forget for knob speed</summary>
    public void StepFreq(long stepHz)
    {
        var f = LastFrequency;
        if (f <= 0) f = GetFreq(); // fallback: query once if no cached value
        if (f <= 0) return;
        var newFreq = f + stepHz;
        LastFrequency = newFreq; // update cache immediately for next step
        SetFreq(newFreq);
    }

    // ========== MODE ==========

    private static readonly Dictionary<char, string> ModeMap = new()
    {
        ['1'] = "LSB", ['2'] = "USB", ['3'] = "CW-U", ['4'] = "FM",
        ['5'] = "AM", ['6'] = "RTTY-L", ['7'] = "CW-L", ['8'] = "DATA-L",
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
        return resp.StartsWith("TX") && resp.Length >= 3 && resp[2] != '0';
    }

    public void SetPTT(bool on) => Send(on ? "TX1;" : "TX0;", false);

    // ========== POWER ==========

    public int GetPower()
    {
        var resp = Send("PC;");
        if (resp.StartsWith("PC") && resp.Length >= 5 && int.TryParse(resp[2..5], out var pwr))
            return pwr;
        return 0;
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
        var resp = Send("RM1;");
        if (resp.StartsWith("RM1") && resp.Length >= 6 && int.TryParse(resp[3..6], out var v)) return v;
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
        var resp = Send("FT;");
        return resp.StartsWith("FT") && resp.Length >= 3 && resp[2] == '1';
    }

    public void SetSplit(bool on) => Send(on ? "FT1;" : "FT0;", false);

    // ========== AF/RF GAIN ==========

    public int GetAFGain()
    {
        var resp = Send("AG0;");
        if (resp.StartsWith("AG0") && resp.Length >= 6 && int.TryParse(resp[3..6], out var v)) return v;
        return 0;
    }

    public void SetAFGain(int level) => Send($"AG0{Math.Clamp(level, 0, 255):D3};", false);

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
            return resp[3] switch { '1' => "OFF", '2' => "FAST", '3' => "MID", '4' => "SLOW", '5' => "AUTO", _ => "?" };
        return "?";
    }

    public void SetAGC(string mode)
    {
        var codes = new Dictionary<string, string>
            { ["OFF"] = "1", ["FAST"] = "2", ["MID"] = "3", ["SLOW"] = "4", ["AUTO"] = "5" };
        if (codes.TryGetValue(mode.ToUpper(), out var c)) Send($"GT0{c};", false);
    }

    // ========== VOX / COMP ==========

    public bool GetVOX() => Send("VX;").Contains("VX1");
    public void SetVOX(bool on) => Send(on ? "VX1;" : "VX0;", false);

    public bool GetComp() => Send("PR;").Contains("PR1");
    public void SetComp(bool on) => Send(on ? "PR1;" : "PR0;", false);

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
    public bool GetCWMemoryStatus() => false; // FTDX-101 doesn't have a CW memory playing query

    public int GetCWPitch()
    {
        var resp = Send("KP;");
        if (resp.StartsWith("KP") && resp.Length >= 4 && int.TryParse(resp[2..4], out var step))
            return 300 + step * 50;
        return 600;
    }

    public void SetCWPitch(int hz) => Send($"KP{(Math.Clamp(hz, 300, 1050) - 300) / 50:D2};", false);
    public int GetBreakIn() { var r = Send("BI;"); return r.StartsWith("BI") && r.Length >= 3 ? r[2] - '0' : 1; }
    public void SetBreakIn(int mode) => Send($"BI{Math.Clamp(mode, 0, 2)};", false);

    // ========== ANTENNA / TUNER / MISC ==========

    public void StartTune() => Send("AC002;", false);
    public void RecallMemory(int num) => Send($"MC{num:D3};", false);
    public void SetWidth(int w) => Send($"SH0{w:D2};", false);

    public int GetAntenna()
    {
        var resp = Send("AN0;");
        if (resp.StartsWith("AN0") && resp.Length >= 4 && int.TryParse(resp[3].ToString(), out var v)) return v;
        return 1;
    }

    public void SetAntenna(int ant) => Send($"AN0{Math.Clamp(ant, 1, 3)};", false);
    public void ToggleAntenna() => SetAntenna(GetAntenna() == 1 ? 2 : 1);

    // ========== PORT DETECTION ==========

    public static string[] GetAvailablePorts() => SerialPort.GetPortNames();

    public void Dispose()
    {
        Disconnect();
        GC.SuppressFinalize(this);
    }
}
