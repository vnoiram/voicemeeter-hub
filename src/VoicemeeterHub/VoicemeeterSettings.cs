using log4net;

namespace VoicemeeterHub;

/// <summary>
///     Channel-key and label helpers shared with clients. The hub only needs the channel-key
///     building and edition-aware abbreviated labels used when composing state snapshots; the
///     full settings parsing lives in each client (for example the Stream Dock plugin).
/// </summary>
public static class VoicemeeterSettings
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(VoicemeeterSettings));

    public const int MaxChannelIndex = 7;

    public static string BuildChannelKey(string channelKind, int channelIndex) => $"{channelKind}:{channelIndex}";

    public static string ShortLabelFor(string channelKind, int channelIndex)
    {
        var prefix = string.Equals(channelKind, "bus", StringComparison.OrdinalIgnoreCase) ? "B" : "S";
        return $"{prefix}{channelIndex}";
    }

    private static readonly string[] StripAbbrStandard = ["HW In 1", "HW In 2", "VM In"];
    private static readonly string[] StripAbbrBanana = ["HW In 1", "HW In 2", "HW In 3", "VM In", "AUX"];
    private static readonly string[] StripAbbrPotato = ["HW In 1", "HW In 2", "HW In 3", "HW In 4", "HW In 5", "VM In", "AUX", "VAIO3"];

    private static readonly string[] BusAbbrStandard = ["Out A1", "Out B1"];
    private static readonly string[] BusAbbrBanana = ["Out A1", "Out A2", "Out A3", "Out B1", "Out B2"];
    private static readonly string[] BusAbbrPotato = ["Out A1", "Out A2", "Out A3", "Out A4", "Out A5", "Out B1", "Out B2", "Out B3"];

    /// <summary>
    ///     Short label matching the channel names shown in the property inspector, abbreviated to
    ///     fit small overview grid cells. Falls back to the generic "S0"/"B0" form for an
    ///     edition/index combination outside the known tables (for example when the edition has
    ///     not been detected yet).
    /// </summary>
    public static string AbbreviatedLabelFor(string channelKind, int channelIndex, VoicemeeterEdition edition)
    {
        var isBus = string.Equals(channelKind, "bus", StringComparison.OrdinalIgnoreCase);
        var table = edition switch
        {
            VoicemeeterEdition.Standard => isBus ? BusAbbrStandard : StripAbbrStandard,
            VoicemeeterEdition.Banana => isBus ? BusAbbrBanana : StripAbbrBanana,
            _ => isBus ? BusAbbrPotato : StripAbbrPotato
        };
        return channelIndex >= 0 && channelIndex < table.Length ? table[channelIndex] : ShortLabelFor(channelKind, channelIndex);
    }

    public static (string Kind, int Index)? ParseChannelKey(string channelKey)
    {
        var parts = channelKey.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[1], out var index)) return null;
        var kind = string.Equals(parts[0], "bus", StringComparison.OrdinalIgnoreCase) ? "bus" : "strip";
        return (kind, Math.Clamp(index, 0, MaxChannelIndex));
    }
}
