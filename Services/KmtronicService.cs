using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace HamDeck.Services;

/// <summary>
/// Controls a KMTronic UDP 8-Channel Relay Board (UD8CR).
/// Commands are sent as ASCII strings over UDP per the official datasheet.
/// Uses bitmask command FFE0xx for atomic single-packet relay switching.
/// </summary>
public class KmtronicService : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private bool _disposed;

    public int ActiveAntenna { get; private set; } = 1;

    // Bitmask commands (FFE0xx) - sets all 8 relays atomically.
    // Bit 0 = relay 1, bit 1 = relay 2, bit 2 = relay 3, etc.
    // ANT 1 = all off (direct path through RAS-4)
    // ANT 2 = relay 1 on  (bit 0 = 0x01)
    // ANT 3 = relay 2 on  (bit 1 = 0x02)
    // ANT 4 = relay 3 on  (bit 2 = 0x04)
    private static readonly string[] AntCommands = { "", "FFE000", "FFE001", "FFE002", "FFE004" };

    public KmtronicService(string host, int port)
    {
        _host = host;
        _port = port;
        Logger.Info("KMTRONIC", "Service created ({0}:{1})", host, port);
    }

    /// <summary>Switch to the specified antenna (1-4). Fire-and-forget.</summary>
    public void SetAntenna(int ant)
    {
        _ = SetAntennaAsync(ant);
    }

    /// <summary>Switch to the specified antenna (1-4). Awaitable.</summary>
    public async Task SetAntennaAsync(int ant)
    {
        if (ant < 1 || ant > 4)
        {
            Logger.Warn("KMTRONIC", "Invalid antenna {0}, must be 1-4", ant);
            return;
        }

        try
        {
            var command = AntCommands[ant];
            Logger.Info("KMTRONIC", "ANT {0} -> sending {1} to {2}:{3}", ant, command, _host, _port);
            await SendCommandAsync(command);
            ActiveAntenna = ant;
            Logger.Info("KMTRONIC", "ANT {0} set OK", ant);
        }
        catch (Exception ex)
        {
            Logger.Error("KMTRONIC", "SetAntenna failed: {0}", ex.Message);
        }
    }

    /// <summary>
    /// Query relay status. Returns 8-char binary string e.g. "00000001".
    /// First char = relay 1, last = relay 8. Returns null on failure.
    /// </summary>
    public async Task<string?> GetStatusAsync()
    {
        try
        {
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = 1000;
            udp.Connect(_host, _port);

            var data = Encoding.ASCII.GetBytes("FF0000");
            await udp.SendAsync(data, data.Length);

            var result = await udp.ReceiveAsync();
            var status = Encoding.ASCII.GetString(result.Buffer).Trim();
            Logger.Debug("KMTRONIC", "Status: {0}", status);
            return status;
        }
        catch (Exception ex)
        {
            Logger.Warn("KMTRONIC", "GetStatus failed: {0}", ex.Message);
            return null;
        }
    }

    private async Task SendCommandAsync(string command)
    {
        using var udp = new UdpClient();
        udp.Connect(_host, _port);
        var data = Encoding.ASCII.GetBytes(command);
        await udp.SendAsync(data, data.Length);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Safe shutdown - turn all relays off (ANT 1 / direct path)
        try
        {
            using var udp = new UdpClient();
            udp.Connect(_host, _port);
            var data = Encoding.ASCII.GetBytes("FFE000");
            udp.Send(data, data.Length);
            Logger.Info("KMTRONIC", "Relays cleared on dispose");
        }
        catch { /* best effort */ }
    }
}
