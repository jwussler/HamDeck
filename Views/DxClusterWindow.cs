using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using HamDeck.Models;
using HamDeck.Services;

namespace HamDeck.Views;

/// <summary>
/// DX Cluster spot list window - fully dark themed with band/mode filtering.
/// </summary>
public class DxClusterWindow : Window
{
    // Colors matching main HamDeck theme
    private static readonly Color DarkBg    = Color.FromRgb(0x1A, 0x1A, 0x2E);
    private static readonly Color CardBg    = Color.FromRgb(0x16, 0x21, 0x3E);
    private static readonly Color InputBg   = Color.FromRgb(0x0F, 0x34, 0x60);
    private static readonly Color HeaderBg  = Color.FromRgb(0x0D, 0x1B, 0x33);
    private static readonly Color CyanAccent = Color.FromRgb(0x00, 0xD4, 0xFF);
    private static readonly Color DimText   = Color.FromRgb(0x88, 0x99, 0xAA);
    private static readonly Color RowHover  = Color.FromRgb(0x1A, 0x2D, 0x50);
    private static readonly Color BorderColor = Color.FromRgb(0x2A, 0x3A, 0x5A);

    private readonly DxClusterClient _cluster;
    private readonly RadioController _radio;
    private readonly Config _config;
    private readonly ListView _spotList;
    private readonly TextBlock _statusBar;
    private readonly TextBlock _freqDisplay;
    private readonly Button _refreshBtn;
    private readonly Button _autoBandBtn;
    private readonly ComboBox _bandFilter;
    private readonly ComboBox _modeFilter;
    private readonly System.Windows.Threading.DispatcherTimer _autoBandTimer;
    private List<DXSpot> _allSpots = new();
    private bool _autoBandEnabled;
    private string _lastAutoBand = "";

    public DxClusterWindow(DxClusterClient cluster, RadioController radio, Config config)
    {
        _cluster = cluster;
        _radio = radio;
        _config = config;

        // CLEANUP FIX: Use config.ClusterCallsign instead of the hardcoded "WA0O" literal.
        // Falls back to "DX Cluster" if the callsign field is not set.
        var callsign = string.IsNullOrWhiteSpace(config.ClusterCallsign)
            ? "DX Cluster"
            : $"DX Cluster \u2014 {config.ClusterCallsign.ToUpperInvariant()}";
        Title = callsign;

        Width = 820;
        Height = 560;
        Background = new SolidColorBrush(DarkBg);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // toolbar
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // filters
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // list
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // status

        // ── Row 0: Toolbar ──
        var toolbar = new Border
        {
            Background = new SolidColorBrush(HeaderBg),
            BorderBrush = new SolidColorBrush(BorderColor),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 6, 8, 6)
        };

        var toolbarPanel = new DockPanel();

        _refreshBtn = MakeButton("\u27F3 Refresh");
        _refreshBtn.Click += RefreshBtn_Click;
        DockPanel.SetDock(_refreshBtn, Dock.Left);
        toolbarPanel.Children.Add(_refreshBtn);

        _freqDisplay = new TextBlock
        {
            Foreground = new SolidColorBrush(CyanAccent),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        DockPanel.SetDock(_freqDisplay, Dock.Left);
        toolbarPanel.Children.Add(_freqDisplay);

        var urlLabel = new TextBlock
        {
            Text = config.ClusterAPIURL,
            Foreground = new SolidColorBrush(DimText),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            FontSize = 10.5
        };
        toolbarPanel.Children.Add(urlLabel);

        toolbar.Child = toolbarPanel;
        Grid.SetRow(toolbar, 0);
        root.Children.Add(toolbar);

        // ── Row 1: Filter bar ──
        var filterBar = new Border
        {
            Background = new SolidColorBrush(CardBg),
            BorderBrush = new SolidColorBrush(BorderColor),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 4, 8, 4)
        };

        var filterPanel = new StackPanel { Orientation = Orientation.Horizontal };

        filterPanel.Children.Add(MakeLabel("Band:"));
        _bandFilter = MakeCombo(new[] { "All" }, 80);
        _bandFilter.SelectionChanged += Filter_Changed;
        filterPanel.Children.Add(_bandFilter);

        filterPanel.Children.Add(MakeLabel("Mode:"));
        _modeFilter = MakeCombo(new[] { "All" }, 80);
        _modeFilter.SelectionChanged += Filter_Changed;
        filterPanel.Children.Add(_modeFilter);

        var curBandBtn = MakeButton("Current Band");
        curBandBtn.Click += CurrentBand_Click;
        curBandBtn.Margin = new Thickness(12, 0, 0, 0);
        filterPanel.Children.Add(curBandBtn);

        _autoBandBtn = MakeButton("Auto Band: OFF");
        _autoBandBtn.Click += AutoBand_Click;
        _autoBandBtn.Margin = new Thickness(4, 0, 0, 0);
        filterPanel.Children.Add(_autoBandBtn);

        var showAllBtn = MakeButton("Show All");
        showAllBtn.Click += (_, _) =>
        {
            SetAutoBand(false);
            _bandFilter.SelectedIndex = 0;
            _modeFilter.SelectedIndex = 0;
        };
        showAllBtn.Margin = new Thickness(4, 0, 0, 0);
        filterPanel.Children.Add(showAllBtn);

        filterBar.Child = filterPanel;
        Grid.SetRow(filterBar, 1);
        root.Children.Add(filterBar);

        // ── Row 2: Spot list ──
        _spotList = new ListView
        {
            Background = new SolidColorBrush(CardBg),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
        };

        var itemStyle = new Style(typeof(ListViewItem));
        itemStyle.Setters.Add(new Setter(ListViewItem.BackgroundProperty, new SolidColorBrush(CardBg)));
        itemStyle.Setters.Add(new Setter(ListViewItem.ForegroundProperty, Brushes.White));
        itemStyle.Setters.Add(new Setter(ListViewItem.BorderThicknessProperty, new Thickness(0)));
        itemStyle.Setters.Add(new Setter(ListViewItem.PaddingProperty, new Thickness(2, 1, 2, 1)));
        itemStyle.Setters.Add(new Setter(ListViewItem.MarginProperty, new Thickness(0)));

        var hoverTrigger = new Trigger { Property = ListViewItem.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(ListViewItem.BackgroundProperty, new SolidColorBrush(RowHover)));
        itemStyle.Triggers.Add(hoverTrigger);

        var selTrigger = new Trigger { Property = ListViewItem.IsSelectedProperty, Value = true };
        selTrigger.Setters.Add(new Setter(ListViewItem.BackgroundProperty, new SolidColorBrush(InputBg)));
        selTrigger.Setters.Add(new Setter(ListViewItem.ForegroundProperty, new SolidColorBrush(CyanAccent)));
        itemStyle.Triggers.Add(selTrigger);

        _spotList.ItemContainerStyle = itemStyle;

        var gridView = new GridView();

        var headerStyle = new Style(typeof(GridViewColumnHeader));
        headerStyle.Setters.Add(new Setter(GridViewColumnHeader.BackgroundProperty, new SolidColorBrush(HeaderBg)));
        headerStyle.Setters.Add(new Setter(GridViewColumnHeader.ForegroundProperty, new SolidColorBrush(CyanAccent)));
        headerStyle.Setters.Add(new Setter(GridViewColumnHeader.BorderBrushProperty, new SolidColorBrush(BorderColor)));
        headerStyle.Setters.Add(new Setter(GridViewColumnHeader.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
        headerStyle.Setters.Add(new Setter(GridViewColumnHeader.PaddingProperty, new Thickness(6, 4, 6, 4)));
        headerStyle.Setters.Add(new Setter(GridViewColumnHeader.FontWeightProperty, FontWeights.Bold));
        headerStyle.Setters.Add(new Setter(GridViewColumnHeader.FontSizeProperty, 11.5));
        gridView.ColumnHeaderContainerStyle = headerStyle;

        gridView.Columns.Add(MakeColumn("Time",    "TimeDisplay",  50));
        gridView.Columns.Add(MakeColumn("Freq",    "DisplayFreq",  80));
        gridView.Columns.Add(MakeColumn("DX Call", "Spotted",      85));
        gridView.Columns.Add(MakeColumn("Entity",  "Entity",      120));
        gridView.Columns.Add(MakeColumn("Mode",    "Mode",         55));
        gridView.Columns.Add(MakeColumn("Band",    "Band",         50));
        gridView.Columns.Add(MakeColumn("Spotter", "Spotter",      80));
        gridView.Columns.Add(MakeColumn("Comment", "ShortMessage", 200));
        _spotList.View = gridView;

        _spotList.MouseDoubleClick += SpotList_DoubleClick;
        Grid.SetRow(_spotList, 2);
        root.Children.Add(_spotList);

        // ── Row 3: Status bar ──
        var statusBorder = new Border
        {
            Background = new SolidColorBrush(HeaderBg),
            BorderBrush = new SolidColorBrush(BorderColor),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(8, 4, 8, 4)
        };

        _statusBar = new TextBlock
        {
            Text = "Loading...",
            Foreground = new SolidColorBrush(DimText),
            FontSize = 11
        };
        statusBorder.Child = _statusBar;
        Grid.SetRow(statusBorder, 3);
        root.Children.Add(statusBorder);

        Content = root;

        _cluster.OnSpotsUpdated += OnSpotsUpdated;

        if (_cluster.Spots.Count > 0)
        {
            OnSpotsUpdated(_cluster.Spots);
        }
        else
        {
            _statusBar.Text = "Fetching spots...";
            Task.Run(async () =>
            {
                var result = await _cluster.FetchNow();
                Dispatcher.Invoke(() => _statusBar.Text = result);
            });
        }

        UpdateFreqDisplay();

        _autoBandTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _autoBandTimer.Tick += AutoBandTimer_Tick;

        Closed += (_, _) =>
        {
            _autoBandTimer.Stop();
            _cluster.OnSpotsUpdated -= OnSpotsUpdated;
        };
    }

    // ── Mode parsing ──
    private static readonly string[] KnownModes = { "FT8", "FT4", "CW", "SSB", "RTTY", "FM", "AM", "C4FM", "DSTAR", "DMR", "JT65", "JT9", "PSK", "SSTV", "JS8" };

    private static string ParseMode(DXSpot spot)
    {
        if (!string.IsNullOrEmpty(spot.Message))
        {
            var msgUpper = spot.Message.ToUpperInvariant();
            foreach (var mode in KnownModes)
                if (msgUpper.Contains(mode)) return mode;
            if (msgUpper.Contains("POTA") || msgUpper.Contains("SOTA") || msgUpper.Contains("WWFF"))
                return IsCWFrequency(spot.Frequency) ? "CW" : "SSB";
        }

        var fKHz = spot.Frequency;
        if (IsFT8Frequency(fKHz)) return "FT8";
        if (IsFT4Frequency(fKHz)) return "FT4";
        if (IsCWFrequency(fKHz)) return "CW";
        return "SSB";
    }

    private static bool IsFT8Frequency(double kHz)
    {
        double[] ft8 = { 1840, 3573, 5357, 7074, 10136, 14074, 18100, 21074, 24915, 28074, 50313, 144174 };
        foreach (var f in ft8) if (Math.Abs(kHz - f) < 3) return true;
        return false;
    }

    private static bool IsFT4Frequency(double kHz)
    {
        double[] ft4 = { 3575.5, 7047.5, 10140, 14080, 18104, 21140, 24919, 28180, 50318 };
        foreach (var f in ft4) if (Math.Abs(kHz - f) < 3) return true;
        return false;
    }

    private static bool IsCWFrequency(double kHz)
    {
        (double lo, double hi)[] cwSegs = {
            (1800, 1850), (3500, 3600), (7000, 7060), (10100, 10140),
            (14000, 14070), (18068, 18095), (21000, 21070), (24890, 24915),
            (28000, 28070), (50000, 50100)
        };
        foreach (var (lo, hi) in cwSegs)
            if (kHz >= lo && kHz <= hi) return true;
        return false;
    }

    // ── Data handling ──

    private void UpdateFreqDisplay()
    {
        if (_radio.Connected)
        {
            var freq = _radio.LastFrequency;
            var band = Helpers.BandHelper.GetBand(freq);
            var mode = _radio.LastMode;
            _freqDisplay.Text = string.Format("{0:N1} kHz | {1} | {2}", freq / 1000.0, band, mode);
        }
        else
        {
            _freqDisplay.Text = "Radio not connected";
        }
    }

    private void OnSpotsUpdated(List<DXSpot> spots)
    {
        Dispatcher.Invoke(() =>
        {
            foreach (var spot in spots)
                spot.Mode = ParseMode(spot);
            _allSpots = spots;
            RebuildFilterDropdowns();
            ApplyFilters();
            UpdateFreqDisplay();
        });
    }

    private void RebuildFilterDropdowns()
    {
        var curBand = _bandFilter.SelectedItem as string ?? "All";
        var curMode = _modeFilter.SelectedItem as string ?? "All";

        var bands = _allSpots
            .Select(s => s.Band)
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(BandSortKey)
            .ToList();

        _bandFilter.SelectionChanged -= Filter_Changed;
        _bandFilter.Items.Clear();
        _bandFilter.Items.Add("All");
        foreach (var b in bands) _bandFilter.Items.Add(b);
        var bandIdx = _bandFilter.Items.IndexOf(curBand);
        _bandFilter.SelectedIndex = bandIdx >= 0 ? bandIdx : 0;
        _bandFilter.SelectionChanged += Filter_Changed;

        var modes = _allSpots
            .Select(s => s.Mode)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m)
            .ToList();

        _modeFilter.SelectionChanged -= Filter_Changed;
        _modeFilter.Items.Clear();
        _modeFilter.Items.Add("All");
        foreach (var m in modes) _modeFilter.Items.Add(m);
        var modeIdx = _modeFilter.Items.IndexOf(curMode);
        _modeFilter.SelectedIndex = modeIdx >= 0 ? modeIdx : 0;
        _modeFilter.SelectionChanged += Filter_Changed;
    }

    private static int BandSortKey(string band)
    {
        var numStr = new string(band.TakeWhile(char.IsDigit).ToArray());
        if (!int.TryParse(numStr, out var num)) return 9999;
        if (band.EndsWith("cm", StringComparison.OrdinalIgnoreCase)) return 10000 + num;
        return 2000 - num;
    }

    private void ApplyFilters()
    {
        var band = _bandFilter.SelectedItem as string ?? "All";
        var mode = _modeFilter.SelectedItem as string ?? "All";

        var filtered = _allSpots.AsEnumerable();
        if (band != "All")
            filtered = filtered.Where(s => s.Band.Equals(band, StringComparison.OrdinalIgnoreCase));
        if (mode != "All")
            filtered = filtered.Where(s => s.Mode.Equals(mode, StringComparison.OrdinalIgnoreCase));

        var list = filtered.ToList();
        _spotList.ItemsSource = list;

        _statusBar.Text = string.Format("{0} spots (of {1}) | Band: {2} | Mode: {3} | Double-click to tune | {4:HH:mm:ss}Z",
            list.Count, _allSpots.Count, band, mode, DateTime.UtcNow);
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender == _bandFilter && _autoBandEnabled)
        {
            var selected = _bandFilter.SelectedItem as string ?? "All";
            if (!selected.Equals(_lastAutoBand, StringComparison.OrdinalIgnoreCase))
                SetAutoBand(false);
        }
        ApplyFilters();
    }

    private void CurrentBand_Click(object sender, RoutedEventArgs e)
    {
        if (!_radio.Connected) return;
        var band = Helpers.BandHelper.GetBand(_radio.LastFrequency);
        if (string.IsNullOrEmpty(band)) return;
        SetAutoBand(false);
        SelectBandInDropdown(band);
    }

    private void AutoBand_Click(object sender, RoutedEventArgs e)
    {
        SetAutoBand(!_autoBandEnabled);
        if (_autoBandEnabled) AutoBandTimer_Tick(this, EventArgs.Empty);
    }

    private void SetAutoBand(bool enabled)
    {
        _autoBandEnabled = enabled;
        if (enabled)
        {
            _autoBandBtn.Background = new SolidColorBrush(CyanAccent);
            _autoBandBtn.Foreground = new SolidColorBrush(DarkBg);
            _autoBandBtn.Content = "Auto Band: ON";
            _autoBandTimer.Start();
        }
        else
        {
            _autoBandBtn.Background = new SolidColorBrush(InputBg);
            _autoBandBtn.Foreground = Brushes.White;
            _autoBandBtn.Content = "Auto Band: OFF";
            _autoBandTimer.Stop();
            _lastAutoBand = "";
        }
    }

    private void AutoBandTimer_Tick(object? sender, EventArgs e)
    {
        if (!_autoBandEnabled || !_radio.Connected) return;
        var band = Helpers.BandHelper.GetBand(_radio.LastFrequency);
        if (string.IsNullOrEmpty(band)) return;
        if (band.Equals(_lastAutoBand, StringComparison.OrdinalIgnoreCase)) return;
        _lastAutoBand = band;
        SelectBandInDropdown(band);
        UpdateFreqDisplay();
    }

    private void SelectBandInDropdown(string band)
    {
        for (int i = 0; i < _bandFilter.Items.Count; i++)
        {
            if (band.Equals(_bandFilter.Items[i] as string, StringComparison.OrdinalIgnoreCase))
            {
                _bandFilter.SelectedIndex = i;
                return;
            }
        }
    }

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        _refreshBtn.IsEnabled = false;
        _statusBar.Text = "Fetching...";
        var result = await _cluster.FetchNow();
        if (!result.StartsWith("OK")) _statusBar.Text = result;
        _refreshBtn.IsEnabled = true;
        UpdateFreqDisplay();
    }

    private void SpotList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_spotList.SelectedItem is DXSpot spot)
        {
            _cluster.TuneToSpot(spot);
            _statusBar.Text = string.Format("\u27F9 Tuned to {0} on {1} kHz ({2})",
                spot.Spotted, spot.DisplayFreq, spot.Mode);
        }
    }

    // ── UI Helpers ──

    private static Button MakeButton(string text) => new Button
    {
        Content = text,
        Padding = new Thickness(10, 3, 10, 3),
        Background = new SolidColorBrush(InputBg),
        Foreground = Brushes.White,
        BorderBrush = new SolidColorBrush(BorderColor),
        BorderThickness = new Thickness(1),
        Cursor = Cursors.Hand,
        FontSize = 11.5
    };

    private static TextBlock MakeLabel(string text) => new TextBlock
    {
        Text = text,
        Foreground = new SolidColorBrush(DimText),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8, 0, 4, 0),
        FontSize = 11.5
    };

    private static ComboBox MakeCombo(string[] items, double width)
    {
        var cb = new ComboBox
        {
            Width = width,
            Background = new SolidColorBrush(InputBg),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(BorderColor),
            FontSize = 11.5,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        foreach (var item in items) cb.Items.Add(item);
        cb.SelectedIndex = 0;
        return cb;
    }

    private static GridViewColumn MakeColumn(string header, string binding, double width) =>
        new GridViewColumn
        {
            Header = header,
            Width = width,
            DisplayMemberBinding = new Binding(binding)
        };
}
