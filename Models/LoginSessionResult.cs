namespace RoomTalk.Models;

public sealed class LoginSessionResult
{
    public bool Success { get; init; }
    public AccountRole Role { get; init; } = AccountRole.User;
    public string Username { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string[] Rooms { get; init; } = [];

    public static LoginSessionResult Failure(string message) => new()
    {
        Success = false,
        Message = message
    };
}
