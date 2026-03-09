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

            // Store listener so it stays alive for the process lifetime
            Current.Properties["InstanceListener"] = listener;

            // Listen for re-show signals from subsequent launch attempts
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
                    catch (ObjectDisposedException) { break; }  // Listener stopped — exit cleanly
                    catch (OperationCanceledException) { break; }
                    // BUG FIX: Any other exception (transient network error, client disconnect,
                    // etc.) should NOT break the loop. Use continue so subsequent launches can
                    // still signal this instance. The original break caused permanent deafness
                    // after the first error.
                    catch { continue; }
                }
            });
        }
        catch
        {
            // Another instance is running — signal it to show and exit
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
