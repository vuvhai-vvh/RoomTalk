namespace RoomTalk.Models;

public sealed class ConnectedClientInfo
{
    public string Username { get; init; } = string.Empty;
    public string MachineName { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    public AccountRole Role { get; init; }
    public string RoleText => Role == AccountRole.Admin ? "Điều hành" : "Người dùng";
    public DateTime ConnectedAt { get; init; }
    public string ConnectedAtText => ConnectedAt.ToString("HH:mm:ss");
}
