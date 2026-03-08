using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HamDeck.Services;

/// <summary>
/// TCP CAT proxy - listens on localhost:4532, forwards CAT commands from N1MM (or any
/// external app) through RadioController's lock so they serialize with HamDeck's own polling.
/// Eliminates the need for VSPE/VSPD virtual serial port splitters.
///
/// N1MM config: Configure Ports → Port = TCP → Set → Host: 127.0.0.1, Port: 4532
/// </summary>
public class TcpCatProxy : IDisposable
{
    private readonly RadioController _radio;
    private readonly int _port;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public bool IsRunning { get; private set; }

    public TcpCatProxy(RadioController radio, int port = 4532)
    {
        _radio = radio;
        _port = port;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, _port);

        try
        {
            _listener.Start();
            IsRunning = true;
            Logger.Info("CATPROXY", "TCP CAT proxy listening on localhost:{0}", _port);
            Task.Run(() => AcceptLoop(_cts.Token));
        }
        catch (Exception ex)
        {
            Logger.Error("CATPROXY", "Failed to start on port {0}: {1}", _port, ex.Message);
            IsRunning = false;
        }
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                Logger.Info("CATPROXY", "Client connected from {0}", client.Client.RemoteEndPoint);
                _ = Task.Run(() => HandleClient(client, ct));
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Logger.Warn("CATPROXY", "Accept error: {0}", ex.Message);
            }
        }
    }

    private void HandleClient(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        client.NoDelay = true;

        var stream = client.GetStream();
        stream.ReadTimeout = 5000;

        var buf = new byte[1024];
        var pending = new StringBuilder();

        Logger.Info("CATPROXY", "Handling client");

        try
        {
            while (!ct.IsCancellationRequested && client.Connected)
            {
                int n;
                try { n = stream.Read(buf, 0, buf.Length); }
                catch (System.IO.IOException) { break; }

                if (n == 0) break;

                pending.Append(Encoding.ASCII.GetString(buf, 0, n));

                // Process all complete CAT commands (terminated by ';')
                string data = pending.ToString();
                int start = 0;

                while (true)
                {
                    int semi = data.IndexOf(';', start);
                    if (semi < 0) break;

                    string cmd = data.Substring(start, semi - start + 1); // includes ';'
                    start = semi + 1;

                    Logger.Debug("CATPROXY", ">>> {0}", cmd.Trim());

                    // Route through RadioController's lock - serializes with HamDeck polling
                    string response = _radio.SendRaw(cmd.Trim());

                    if (!string.IsNullOrEmpty(response))
                    {
                        Logger.Debug("CATPROXY", "<<< {0}", response);
                        var respBytes = Encoding.ASCII.GetBytes(response);
                        try { stream.Write(respBytes, 0, respBytes.Length); }
                        catch { break; }
                    }
                }

                // Keep any incomplete command fragment
                pending.Clear();
                if (start < data.Length)
                    pending.Append(data.Substring(start));
            }
        }
        catch (Exception ex)
        {
            Logger.Debug("CATPROXY", "Client error: {0}", ex.Message);
        }

        Logger.Info("CATPROXY", "Client disconnected");
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        IsRunning = false;
        Logger.Info("CATPROXY", "Stopped");
    }
}
