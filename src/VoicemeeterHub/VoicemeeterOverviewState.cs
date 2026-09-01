using System.Text.Json.Serialization;

namespace VoicemeeterHub;

public sealed record VoicemeeterOverviewState(
    string ChannelKey,
    string ShortLabel,
    double? GainDb,
    bool? Muted,
    string? Error)
{
    [JsonIgnore]
    public bool Ok => Error == null;

    [JsonIgnore]
    public string ValueText
    {
        get
        {
            if (Error != null) return "ERR";
            if (Muted == true) return "M";
            var rounded = Math.Round(GainDb ?? 0);
            return rounded > 0 ? $"+{rounded:0}" : $"{rounded:0}";
        }
    }
}
