namespace RoomTalk.Models;

public sealed class CallSessionState
{
    public Guid CallId { get; init; }
    public bool IsActive { get; init; }
    public string RoomName { get; init; } = string.Empty;
    public string InitiatorUsername { get; init; } = string.Empty;
    public string RecipientUsername { get; init; } = string.Empty;
    public bool IsCurrentUserInitiator { get; init; }
    public string Message { get; init; } = string.Empty;
}
