using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace HamDeck.Models;

public class SessionStats
{
    public DateTime SessionStart { get; set; } = DateTime.UtcNow;
    public int QSYCount { get; set; }
    public int PTTCount { get; set; }
    public int QSOCount { get; set; }
    public TimeSpan TotalTXTime { get; set; }
    public Dictionary<string, int> BandChanges { get; set; } = new();
    public Dictionary<string, int> ModeChanges { get; set; } = new();

    private DateTime? _lastTxStart;

    public void RecordTXStart() { _lastTxStart = DateTime.UtcNow; PTTCount++; }
    public void RecordTXEnd()
    {
        if (_lastTxStart.HasValue)
        {
            TotalTXTime += DateTime.UtcNow - _lastTxStart.Value;
            _lastTxStart = null;
        }
    }

    public void RecordBandChange(string band)
    {
        QSYCount++;
        BandChanges[band] = BandChanges.GetValueOrDefault(band) + 1;
    }

    public void RecordModeChange(string mode)
    {
        ModeChanges[mode] = ModeChanges.GetValueOrDefault(mode) + 1;
    }

    public void RecordQSO() { QSOCount++; }

    public string SessionDuration => (DateTime.UtcNow - SessionStart).ToString(@"hh\:mm\:ss");
    public string TXTimeDisplay => TotalTXTime.ToString(@"hh\:mm\:ss");

    public void Save()
    {
        try
        {
            var path = Path.Combine(Config.ConfigDir, "session_stats.json");
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, JsonSerializer.Serialize(this, opts));
        }
        catch { /* ignore */ }
    }
}
