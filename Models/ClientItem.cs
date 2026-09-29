namespace RoomTalk.Models;

public sealed class ClientItem
{
    public string Account { get; init; } = string.Empty;
    public string ComputerName { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    public string RoomName { get; init; } = string.Empty;
    public bool HasMicrophone { get; init; }
    public bool HasSpeaker { get; init; }
    public bool IsOnline { get; init; }

    public string StatusText => IsOnline ? "Đang kết nối" : "Chưa kết nối";
}
