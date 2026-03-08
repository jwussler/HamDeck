using System;
using System.Collections.Generic;
using System.Globalization;

namespace HamDeck.Helpers;

public static class BandHelper
{
    private static readonly (long Min, long Max, string Name)[] Bands =
    [
        (1_800_000, 2_000_000, "160m"),
        (3_500_000, 4_000_000, "80m"),
        (5_300_000, 5_500_000, "60m"),
        (7_000_000, 7_300_000, "40m"),
        (10_100_000, 10_150_000, "30m"),
        (14_000_000, 14_350_000, "20m"),
        (18_068_000, 18_168_000, "17m"),
        (21_000_000, 21_450_000, "15m"),
        (24_890_000, 24_990_000, "12m"),
        (28_000_000, 29_700_000, "10m"),
        (50_000_000, 54_000_000, "6m"),
    ];

    public static string GetBand(long freqHz)
    {
        foreach (var (min, max, name) in Bands)
            if (freqHz >= min && freqHz <= max) return name;
        return "";
    }

    /// <summary>
    /// Returns the default mode for a given frequency used when switching bands via
    /// the band buttons or entering a frequency manually.
    ///
    /// NOTE: This deliberately returns USB for all frequencies above 10 MHz,
    /// including frequencies that fall within CW sub-bands (e.g. 14.000–14.070).
    /// That is intentional: the BandFrequencies presets below all target the phone
    /// calling frequencies for each band, so the "wrong" CW-segment edge case never
    /// arises in normal use. If you add a CW band-preset, call SetMode("CW")
    /// explicitly rather than relying on this helper.
    ///
    /// Special cases:
    ///   - 60m (5.3–5.5 MHz) is USB by IARU/FCC plan (not LSB like other HF below 10 MHz)
    ///   - 30m (10.1–10.15 MHz) is CW/digital only — returns "CW" for completeness,
    ///     though digital ops will want to set DATA-U manually after tuning
    /// </summary>
    public static string GetModeForFrequency(long freqHz)
    {
        if (freqHz >= 5_300_000 && freqHz <= 5_500_000) return "USB"; // 60m — USB per band plan
        if (freqHz >= 10_100_000 && freqHz <= 10_150_000) return "CW"; // 30m — CW/digital only
        return freqHz < 10_000_000 ? "LSB" : "USB";
    }

    public static string RawToSUnit(int raw)
    {
        if (raw <= 0) return "S0";
        const int s9 = 120;
        if (raw < s9)
        {
            int sUnit = Math.Clamp(raw * 9 / s9, 1, 9);
            return $"S{sUnit}";
        }
        int dbOver = (raw - s9) * 60 / (255 - s9);
        dbOver = (dbOver + 5) / 10 * 10;
        dbOver = Math.Min(dbOver, 60);
        return dbOver <= 0 ? "S9" : $"S9+{dbOver}";
    }

    /// <summary>Phone band presets — center of the SSB phone segment for each band.
    /// These are the frequencies the band buttons tune to. All target the phone calling
    /// frequency rather than the CW or digital segment — see GetModeForFrequency().</summary>
    public static readonly Dictionary<string, long> BandFrequencies = new()
    {
        ["160"] = 1_880_000,   // 160m phone: 1.800-2.000, center ~1.880
        ["80"]  = 3_860_000,   //  80m phone: 3.600-4.000, center ~3.860
        ["60"]  = 5_330_500,   //  60m channelized: 5.330.5
        ["40"]  = 7_200_000,   //  40m phone: 7.125-7.300, center ~7.200
        ["30"]  = 10_130_000,  //  30m CW/digital only: 10.130
        ["20"]  = 14_200_000,  //  20m phone: 14.150-14.350, center ~14.200
        ["17"]  = 18_130_000,  //  17m phone: 18.110-18.168, center ~18.130
        ["15"]  = 21_300_000,  //  15m phone: 21.200-21.450, center ~21.300
        ["12"]  = 24_940_000,  //  12m phone: 24.930-24.990, center ~24.940
        ["10"]  = 28_400_000,  //  10m phone: 28.300-29.700, center ~28.400
        ["6"]   = 50_125_000   //   6m SSB calling: 50.125
    };
}

public static class FrequencyHelper
{
    /// <summary>Parse flexible user input to Hz. Supports MHz (14.200), kHz (14200), or Hz (14200000).</summary>
    public static long Parse(string input)
    {
        input = input.Trim().Replace(",", "");
        if (string.IsNullOrEmpty(input)) return 0;

        if (input.Contains('.'))
        {
            var parts = input.Split('.');
            if (parts.Length == 2 && long.TryParse(parts[0], out var intPart))
            {
                if (double.TryParse(input, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var f))
                {
                    return intPart < 100 ? (long)(f * 1_000_000) : (long)(f * 1_000);
                }
            }
            return 0;
        }

        if (!long.TryParse(input, out var val)) return 0;
        if (val < 100) return val * 1_000_000;
        if (val < 100_000) return val * 1_000;
        return val;
    }

    public static string FormatMHz(long hz) => $"{hz / 1_000_000.0:F3} MHz";
    public static string FormatKHz(long hz) => $"{hz / 1_000.0:F1} kHz";
}
