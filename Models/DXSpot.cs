using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HamDeck.Models;

/// <summary>Handles JSON frequency values that can be int (144174), float (14074.5), or string ("14074")</summary>
public class FlexibleDoubleConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.GetDouble();
            case JsonTokenType.String:
                var s = reader.GetString();
                if (double.TryParse(s, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var d))
                    return d;
                return 0;
            default:
                return 0;
        }
    }

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}

/// <summary>DXCC entity info from API</summary>
public class DXCCInfo
{
    [JsonPropertyName("cont")] public string Continent { get; set; } = "";
    [JsonPropertyName("entity")] public string Entity { get; set; } = "";
    [JsonPropertyName("flag")] public string Flag { get; set; } = "";
    [JsonPropertyName("cqz")] public string CQZone { get; set; } = "";
    [JsonPropertyName("pota_ref")] public string POTARef { get; set; } = "";
    [JsonPropertyName("pota_mode")] public string POTAMode { get; set; } = "";
}

/// <summary>DX Cluster spot from WA0O JSON API</summary>
public class DXSpot
{
    [JsonPropertyName("spotter")] public string Spotter { get; set; } = "";
    [JsonPropertyName("spotted")] public string Spotted { get; set; } = "";
    [JsonPropertyName("frequency"), JsonConverter(typeof(FlexibleDoubleConverter))]
    public double Frequency { get; set; } // kHz - handles int, float, or string from JSON
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("when")] public string When { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("band")] public string Band { get; set; } = "";
    [JsonPropertyName("dxcc_spotter")] public DXCCInfo? DXCCSpotter { get; set; }
    [JsonPropertyName("dxcc_spotted")] public DXCCInfo? DXCCSpotted { get; set; }

    // Computed fields
    [JsonIgnore] public double FreqKHz => Frequency;
    [JsonIgnore] public long FreqHz => (long)(Frequency * 1000);
    [JsonIgnore] public string DisplayFreq => string.Format("{0:F1}", Frequency);
    [JsonIgnore] public string Flag => DXCCSpotted?.Flag ?? "";
    [JsonIgnore] public string Entity => DXCCSpotted?.Entity ?? "";
    [JsonIgnore] public string BandName => string.IsNullOrEmpty(Band) ? Helpers.BandHelper.GetBand(FreqHz) : Band;

    // Mode — inferred at parse time
    [JsonIgnore] public string Mode { get; set; } = "";

    // Time — parsed from When
    [JsonIgnore] public DateTime Time { get; set; }

    /// <summary>Short time display (HH:mm UTC)</summary>
    [JsonIgnore] public string TimeDisplay => Time == default ? "" : Time.ToString("HH:mm");

    /// <summary>Truncated comment for display</summary>
    [JsonIgnore] public string ShortMessage => Message.Length > 35 ? Message[..35] + "…" : Message;
}
