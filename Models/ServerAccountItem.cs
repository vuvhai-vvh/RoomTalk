namespace RoomTalk.Models;

public sealed class ServerAccountItem
{
    public string Username { get; init; } = string.Empty;
    public AccountRole Role { get; init; }
    public string RoleText => Role switch
    {
        AccountRole.Server => "Máy chủ",
        AccountRole.Admin => "Điều hành",
        _ => "Người dùng"
    };

    public string CommunicationName => Role == AccountRole.Server ? "—" : Username;
    public bool Enabled { get; init; }
    public string StatusText => Enabled ? "Đang dùng" : "Đã khóa";
}
