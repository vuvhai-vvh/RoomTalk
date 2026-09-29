namespace RoomTalk.Models;

public sealed class AudioPresetItem
{
    public required AudioProcessingPreset Value { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
}
