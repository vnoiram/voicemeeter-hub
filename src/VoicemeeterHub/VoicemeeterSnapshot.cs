namespace VoicemeeterHub;

public sealed record VoicemeeterSnapshot(
    DateTimeOffset UpdatedAt,
    VoicemeeterEdition Edition,
    IReadOnlyDictionary<string, VoicemeeterOverviewState> CurrentStates,
    string? Error)
{
    public bool TryGetState(string channelKey, out VoicemeeterOverviewState state)
    {
        return CurrentStates.TryGetValue(channelKey, out state!);
    }
}
