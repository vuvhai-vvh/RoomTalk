namespace RoomTalk.Models;

public sealed record AudioTestResult(bool IsSuccess, string Message)
{
    public static AudioTestResult Success(string message) => new(true, message);
    public static AudioTestResult Failure(string message) => new(false, message);
}
