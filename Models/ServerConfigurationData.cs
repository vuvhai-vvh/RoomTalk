namespace RoomTalk.Models;

public sealed class ServerConfigurationData
{
    public int Version { get; set; } = 2;
    public int Port { get; set; } = 5000;

    // Chỉ giữ để tương thích dữ liệu V11 trở về trước. V12 không dùng danh sách phòng riêng.
    public List<RoomDefinition> Rooms { get; set; } = [];

    public List<ServerAccountRecord> Accounts { get; set; } = [];
}
