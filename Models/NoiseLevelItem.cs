namespace RoomTalk.Models;

public sealed class NoiseLevelItem
{
    public required AudioNoiseSuppressionLevel Value { get; init; }
    public required string Name { get; init; }
}
