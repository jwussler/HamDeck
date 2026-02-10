using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace HamDeck;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Parse command line for --silent / -s
        bool startSilent = e.Args.Contains("--silent") || e.Args.Contains("-s");

        // Single-instance check via TCP port 5099
        try
        {
            var listener = new System.Net.Sockets.TcpListener(
                System.Net.IPAddress.Loopback, 5099);
            listener.Start();

            // Store listener so it stays alive
            Current.Properties["InstanceListener"] = listener;

            // Listen for other instances
            Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        var client = await listener.AcceptTcpClientAsync();
                        var buf = new byte[32];
                        var stream = client.GetStream();
                        int n = await stream.ReadAsync(buf);
                        client.Close();

                        if (System.Text.Encoding.ASCII.GetString(buf, 0, n) == "SHOW")
                        {
                            Current.Dispatcher.Invoke(() =>
                            {
                                MainWindow?.Show();
                                MainWindow?.Activate();
                                if (MainWindow?.WindowState == WindowState.Minimized)
                                    MainWindow.WindowState = WindowState.Normal;
                            });
                        }
                    }
                    catch { break; }
                }
            });
        }
        catch
        {
            // Another instance is running - signal it and exit
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                client.Connect(System.Net.IPAddress.Loopback, 5099);
                var buf = System.Text.Encoding.ASCII.GetBytes("SHOW");
                client.GetStream().Write(buf);
            }
            catch { }

            Shutdown();
            return;
        }

        // Store silent flag for main window
        Current.Properties["StartSilent"] = startSilent;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Current.Properties["InstanceListener"] is System.Net.Sockets.TcpListener listener)
        {
            try { listener.Stop(); } catch { }
        }
        base.OnExit(e);
    }
}
