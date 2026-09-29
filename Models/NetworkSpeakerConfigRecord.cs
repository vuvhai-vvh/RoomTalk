namespace RoomTalk.Models;

public sealed class NetworkSpeakerConfigRecord
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public int ManagementPort { get; set; } = 80;
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public string RoomName { get; set; } = "Chưa gán phòng";
    public NetworkSpeakerStatus Status { get; set; } = NetworkSpeakerStatus.Unknown;
    public DateTimeOffset? LastSeen { get; set; }
    public string Note { get; set; } = string.Empty;

    public static NetworkSpeakerConfigRecord FromItem(NetworkSpeakerItem item)
    {
        return new NetworkSpeakerConfigRecord
        {
            Id = item.Id,
            Name = item.Name,
            IpAddress = item.IpAddress,
            ManagementPort = item.ManagementPort,
            Manufacturer = item.Manufacturer,
            Model = item.Model,
            Protocol = item.Protocol,
            RoomName = item.RoomName,
            Status = item.Status,
            LastSeen = item.LastSeen,
            Note = item.Note
        };
    }

    public NetworkSpeakerItem ToItem()
    {
        return new NetworkSpeakerItem
        {
            Id = Id == Guid.Empty ? Guid.NewGuid() : Id,
            Name = string.IsNullOrWhiteSpace(Name) ? "Loa IP" : Name,
            IpAddress = IpAddress,
            ManagementPort = ManagementPort,
            Manufacturer = string.IsNullOrWhiteSpace(Manufacturer) ? "Chưa xác định" : Manufacturer,
            Model = string.IsNullOrWhiteSpace(Model) ? "Chưa xác định" : Model,
            Protocol = Protocol,
            RoomName = string.IsNullOrWhiteSpace(RoomName) ? "Chưa gán phòng" : RoomName,
            Status = Status,
            LastSeen = LastSeen,
            Note = Note
        };
    }
}
