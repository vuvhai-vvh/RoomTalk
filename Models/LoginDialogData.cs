namespace RoomTalk.Models;

public sealed class LoginDialogData
{
    public string ServerIp { get; init; } = "127.0.0.1";
    public string ServerPort { get; init; } = "5000";
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
}
