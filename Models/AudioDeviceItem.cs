namespace RoomTalk.Models;

public sealed class AudioDeviceItem
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsDefault { get; init; }

    public string DisplayName => IsDefault ? $"{Name} (Mặc định)" : Name;
}
