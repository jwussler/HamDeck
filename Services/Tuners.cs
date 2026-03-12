using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using HamDeck.Models;

namespace HamDeck.Services;

/// <summary>TG-XL external antenna tuner control via TCP</summary>
public class TgxlTuner
{
    private readonly RadioController _radio;
    private readonly Config _config;
    private readonly object _lock = new();
    private volatile bool _stopFlag;

    public bool IsActive { get; private set; }

    public TgxlTuner(RadioController radio, Config config)
    {
        _radio = radio;
        _config = config;
    }

    public void Stop() { lock (_lock) _stopFlag = true; }

    public Dictionary<string, object> Tune()
    {
        if (IsActive)
        {
            Logger.Info("TGXL", "Stopping (toggle)");
            Stop();
            // Don't block UI thread - fire and forget the wait
            Task.Run(() => Thread.Sleep(500));
            return new() { ["ok"] = true, ["action"] = "stopped", ["tuning"] = false };
        }

        Logger.Info("TGXL", "Tune() called - launching worker");
        Task.Run(TuneWorker);
        return new() { ["ok"] = true, ["action"] = "started", ["tuning"] = true };
    }

    private void TuneWorker()
    {
        lock (_lock) { IsActive = true; _stopFlag = false; }

        int origPower = 0;
        string origMode = "";
        bool stateChanged = false;

        try
        {
            if (string.IsNullOrEmpty(_config.TGXLHost))
            { Logger.Warn("TGXL", "Host not configured"); return; }

            Logger.Info("TGXL", "Starting - target {0}:{1}", _config.TGXLHost, _config.TGXLPort);

            // Save state
            origPower = _radio.GetPower();
            origMode = _radio.GetMode();
            Logger.Info("TGXL", "Saved - Power: {0}W, Mode: {1}", origPower, origMode);

            // Set tune mode
            Logger.Info("TGXL", "Setting 15W CW mode");
            _radio.SetPower(15); Thread.Sleep(200);
            _radio.SetMode("CW"); Thread.Sleep(200);
            stateChanged = true;

            // Key PTT
            _radio.SetPTT(true); Thread.Sleep(500);
            Logger.Info("TGXL", "PTT ON");

            // Connect to TGXL with timeout (matching Go's 3s DialTimeout)
            using var client = new TcpClient();
            Logger.Info("TGXL", "Connecting to {0}:{1}...", _config.TGXLHost, _config.TGXLPort);

            var connectResult = client.BeginConnect(_config.TGXLHost, _config.TGXLPort, null, null);
            bool connected = connectResult.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(3));
            if (!connected || !client.Connected)
            {
                Logger.Error("TGXL", "Connection TIMEOUT after 3s");
                _radio.SetPTT(false);
                RestoreState(origPower, origMode);
                return;
            }
            client.EndConnect(connectResult);

            using var stream = client.GetStream();
            stream.ReadTimeout = 2000;

            Logger.Info("TGXL", "Connected to TGXL");

            // Send autotune command - use raw bytes with \n only (NOT \r\n!)
            // Python: sock.sendall(b"C1|autotune\n")
            // Go:     conn.Write([]byte("C1|autotune\n"))
            byte[] autotuneCmd = System.Text.Encoding.ASCII.GetBytes("C1|autotune\n");
            stream.Write(autotuneCmd, 0, autotuneCmd.Length);
            stream.Flush();
            Logger.Info("TGXL", "Sent autotune command");
            Thread.Sleep(200);

            var startTime = DateTime.UtcNow;
            bool tuningSeen = false;
            int pollCount = 0;
            bool tuneComplete = false;
            string buffer = "";

            Logger.Info("TGXL", "Reading status stream...");

            while (!_stopFlag && !tuneComplete)
            {
                var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                if (elapsed >= 45) { Logger.Warn("TGXL", "TIMEOUT after {0:F1}s", elapsed); break; }

                try
                {
                    // Read raw bytes like Python's sock.recv(1024)
                    var readBuf = new byte[1024];
                    int bytesRead = stream.Read(readBuf, 0, readBuf.Length);
                    if (bytesRead == 0)
                    {
                        Logger.Info("TGXL", "Connection closed by TGXL");
                        break;
                    }

                    buffer += System.Text.Encoding.ASCII.GetString(readBuf, 0, bytesRead);

                    // Process complete lines (matching Python's buffer approach)
                    while (buffer.Contains('\n'))
                    {
                        int nlIdx = buffer.IndexOf('\n');
                        string line = buffer[..nlIdx].Trim();
                        buffer = buffer[(nlIdx + 1)..];

                        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("V")) continue;
                        pollCount++;

                        // Parse tuning value from this line
                        string? tuningValue = null;
                        foreach (var part in line.Split())
                            if (part.StartsWith("tuning="))
                            { tuningValue = part["tuning=".Length..]; break; }
                        if (tuningValue == null) continue;

                        // Log every 10th status or when state changes
                        if (pollCount % 10 == 1)
                            Logger.Info("TGXL", "Status #{0} ({1:F1}s) - tuning={2}", pollCount, elapsed, tuningValue);

                        // Check if tuning started
                        if (tuningValue == "1" && !tuningSeen)
                        {
                            Logger.Info("TGXL", "*** TUNING ACTIVE (status #{0}) ***", pollCount);
                            tuningSeen = true;
                        }

                        // Check if tuning completed
                        // GUARD: The TG-XL sends an initial burst of status messages
                        // (tuning=0, tuning=1, tuning=0) all within milliseconds of
                        // connecting. Ignore the 1→0 transition until at least 2s have
                        // elapsed — real tuning takes 3-15 seconds.
                        if (tuningValue == "0")
                        {
                            if (tuningSeen && elapsed >= 2.0)
                            {
                                Logger.Info("TGXL", "*** COMPLETE - tuning went 1→0 (status #{0}, {1:F1}s) ***", pollCount, elapsed);
                                tuneComplete = true;
                                break;
                            }
                            else if (tuningSeen && elapsed < 2.0)
                            {
                                Logger.Debug("TGXL", "Ignoring early 1→0 at {0:F1}s (initial burst)", elapsed);
                                tuningSeen = false; // Reset — wait for the real tuning=1
                            }
                            else if (elapsed > 5)
                            {
                                Logger.Info("TGXL", "*** COMPLETE - 5s timeout (status #{0}, {1:F1}s) ***", pollCount, elapsed);
                                tuneComplete = true;
                                break;
                            }
                        }
                    }
                }
                catch (IOException)
                {
                    // Read timeout - request fresh status (matching Python/Go behavior)
                    try
                    {
                        byte[] statusCmd = System.Text.Encoding.ASCII.GetBytes("C1|status\n");
                        stream.Write(statusCmd, 0, statusCmd.Length);
                        stream.Flush();
                    }
                    catch
                    {
                        Logger.Info("TGXL", "Connection lost, assuming complete");
                        break;
                    }
                }
            }

            // Stop transmitting IMMEDIATELY
            Logger.Info("TGXL", "*** STOPPING PTT NOW ***");
            _radio.SetPTT(false); Thread.Sleep(300);
            Logger.Info("TGXL", "PTT OFF");

            // Restore state
            RestoreState(origPower, origMode);

            Logger.Info("TGXL", "Complete - {0} status updates, {1:F1}s total",
                pollCount, (DateTime.UtcNow - startTime).TotalSeconds);
        }
        catch (Exception ex)
        {
            Logger.Error("TGXL", "EXCEPTION: {0}", ex.Message);
            Logger.Error("TGXL", "Stack: {0}", ex.StackTrace ?? "");

            // Always turn off PTT on error
            try { _radio.SetPTT(false); } catch { }
            Logger.Info("TGXL", "PTT OFF (exception)");

            // Restore state if we changed it
            if (stateChanged)
            {
                try { RestoreState(origPower, origMode); } catch { }
            }
        }
        finally
        {
            lock (_lock) { IsActive = false; _stopFlag = false; }
        }
    }

    private void RestoreState(int power, string mode)
    {
        Logger.Info("TGXL", "Restoring - Power: {0}W, Mode: {1}", power, mode);
        _radio.SetPower(power); Thread.Sleep(200);
        _radio.SetMode(mode); Thread.Sleep(200);

        // Verify restoration (matching Go behavior)
        var verifyMode = _radio.GetMode();
        var verifyPower = _radio.GetPower();
        Logger.Info("TGXL", "Verified - Power: {0}W, Mode: {1}", verifyPower, verifyMode);
    }
}

/// <summary>Amplifier tune sequence - keys PTT in CW at low power for a duration</summary>
public class AmpTuner
{
    private readonly RadioController _radio;
    private readonly object _lock = new();
    private volatile bool _stopFlag;

    public bool IsActive { get; private set; }

    public AmpTuner(RadioController radio) => _radio = radio;

    public void Stop() { lock (_lock) _stopFlag = true; }

    public Dictionary<string, object> Tune(int seconds = 30, double powerPercent = 0.15)
    {
        if (IsActive)
        {
            Logger.Info("AMP", "Stopping (toggle)");
            Stop();
            Task.Run(() => Thread.Sleep(500));
            return new() { ["ok"] = true, ["action"] = "stopped", ["tuning"] = false };
        }

        seconds = Math.Clamp(seconds, 1, 60);
        int watts = Math.Clamp((int)(powerPercent * 100), 5, 100);

        Task.Run(() => TuneWorker(seconds, watts));
        return new() { ["ok"] = true, ["action"] = "started", ["tuning"] = true, ["duration"] = seconds, ["power"] = watts };
    }

    private void TuneWorker(int seconds, int tuneWatts)
    {
        lock (_lock) { IsActive = true; _stopFlag = false; }

        try
        {
            Logger.Info("AMP", "Starting for {0}s at {1}W", seconds, tuneWatts);

            int origPower = _radio.GetPower();
            string origMode = _radio.GetMode();
            Logger.Info("AMP", "Saved - Power: {0}W, Mode: {1}", origPower, origMode);

            _radio.SetPower(tuneWatts); Thread.Sleep(200);
            _radio.SetMode("CW"); Thread.Sleep(200);
            _radio.SetPTT(true);
            Logger.Info("AMP", "PTT ON");

            for (int i = 0; i < seconds * 10; i++)
            {
                if (_stopFlag) { Logger.Info("AMP", "Stopped early"); break; }
                Thread.Sleep(100);
            }

            _radio.SetPTT(false); Thread.Sleep(300);
            Logger.Info("AMP", "PTT OFF");

            _radio.SetPower(origPower); Thread.Sleep(200);
            _radio.SetMode(origMode); Thread.Sleep(200);

            var verifyMode = _radio.GetMode();
            var verifyPower = _radio.GetPower();
            Logger.Info("AMP", "Complete - Verified: {0}W, {1}", verifyPower, verifyMode);
        }
        catch (Exception ex)
        {
            Logger.Error("AMP", "Error: {0}", ex.Message);
            try { _radio.SetPTT(false); } catch { }
        }
        finally
        {
            lock (_lock) { IsActive = false; _stopFlag = false; }
        }
    }
}
