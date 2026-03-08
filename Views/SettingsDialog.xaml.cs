using System.IO.Ports;
using System.Windows;
using HamDeck.Models;

namespace HamDeck.Views;

public partial class SettingsDialog : Window
{
    public Config Config { get; private set; }

    public SettingsDialog(Config config)
    {
        InitializeComponent();
        Config = config;
        LoadValues();
    }

    private void LoadValues()
    {
        // Populate port lists
        var ports = SerialPort.GetPortNames();
        foreach (var p in ports) { RadioPortBox.Items.Add(p); FlexknobPortBox.Items.Add(p); }
        RadioPortBox.Text = Config.RadioPort;
        FlexknobPortBox.Text = Config.FlexknobPort;

        // Baud rates
        int[] bauds = [4800, 9600, 19200, 38400, 57600, 115200];
        foreach (var b in bauds) BaudBox.Items.Add(b);
        BaudBox.SelectedItem = Config.RadioBaud;

        // API
        ApiEnabledBox.IsChecked = Config.APIEnabled;
        ApiPortBox.Text = Config.APIPort.ToString();

        // TCP CAT Proxy
        CatProxyEnabledBox.IsChecked = Config.CatProxyEnabled;
        CatProxyPortBox.Text = Config.CatProxyPort.ToString();

        // Wavelog
        WavelogEnabledBox.IsChecked = Config.WavelogEnabled;
        WavelogUrlBox.Text = Config.WavelogURL;
        WavelogKeyBox.Text = Config.WavelogAPIKey;
        WavelogStationBox.Text = Config.WavelogStationID.ToString();

        // TG-XL
        TgxlHostBox.Text = Config.TGXLHost;
        TgxlPortBox.Text = Config.TGXLPort.ToString();

        // KMTronic
        KmtronicEnabledBox.IsChecked = Config.KmtronicEnabled;
        KmtronicHostBox.Text = Config.KmtronicHost;
        KmtronicPortBox.Text = Config.KmtronicPort.ToString();

        // Cluster
        ClusterEnabledBox.IsChecked = Config.ClusterEnabled;
        ClusterUrlBox.Text = Config.ClusterAPIURL;

        // FlexKnob
        FlexknobEnabledBox.IsChecked = Config.FlexknobEnabled;

        int[] flexBauds = [4800, 9600, 19200, 38400, 57600, 115200];
        foreach (var b in flexBauds) FlexknobBaudBox.Items.Add(b);
        FlexknobBaudBox.SelectedItem = Config.FlexknobBaud;

        int[] flexSteps = [10, 50, 100, 500, 1000, 5000, 10000];
        foreach (var s in flexSteps) FlexknobStepBox.Items.Add(s);
        FlexknobStepBox.SelectedItem = Config.FlexknobDefaultStep;

        // Window
        StartMinBox.IsChecked = Config.StartMinimized;
        TrayBox.IsChecked = Config.MinimizeToTray;

        // Logging
        LogFileBox.IsChecked = Config.LogToFile;
        DebugBox.IsChecked = Config.LogLevel == "debug";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Config.RadioPort = RadioPortBox.Text;
        if (BaudBox.SelectedItem is int baud) Config.RadioBaud = baud;

        Config.APIEnabled = ApiEnabledBox.IsChecked == true;
        if (int.TryParse(ApiPortBox.Text, out var port)) Config.APIPort = port;

        Config.CatProxyEnabled = CatProxyEnabledBox.IsChecked == true;
        if (int.TryParse(CatProxyPortBox.Text, out var proxyPort)) Config.CatProxyPort = proxyPort;

        Config.WavelogEnabled = WavelogEnabledBox.IsChecked == true;
        Config.WavelogURL = WavelogUrlBox.Text;
        Config.WavelogAPIKey = WavelogKeyBox.Text;
        if (int.TryParse(WavelogStationBox.Text, out var sid)) Config.WavelogStationID = sid;

        Config.TGXLHost = TgxlHostBox.Text;
        if (int.TryParse(TgxlPortBox.Text, out var tp)) Config.TGXLPort = tp;

        Config.KmtronicEnabled = KmtronicEnabledBox.IsChecked == true;
        Config.KmtronicHost = KmtronicHostBox.Text;
        if (int.TryParse(KmtronicPortBox.Text, out var kp)) Config.KmtronicPort = kp;

        Config.ClusterEnabled = ClusterEnabledBox.IsChecked == true;
        Config.ClusterAPIURL = ClusterUrlBox.Text;

        Config.FlexknobEnabled = FlexknobEnabledBox.IsChecked == true;
        Config.FlexknobPort = FlexknobPortBox.Text;
        if (FlexknobBaudBox.SelectedItem is int fb) Config.FlexknobBaud = fb;
        if (FlexknobStepBox.SelectedItem is int fs) Config.FlexknobDefaultStep = fs;

        Config.StartMinimized = StartMinBox.IsChecked == true;
        Config.MinimizeToTray = TrayBox.IsChecked == true;

        Config.LogToFile = LogFileBox.IsChecked == true;
        Config.LogLevel = DebugBox.IsChecked == true ? "debug" : "info";

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
