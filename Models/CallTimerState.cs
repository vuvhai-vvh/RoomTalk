namespace RoomTalk.Models;

public sealed class CallTimerState
{
    public Guid CallId { get; init; }
    public string RoomName { get; init; } = string.Empty;
    public int SecondsRemaining { get; init; }
    public bool IsExtensionConfirmationRequired { get; init; }
    public bool IsCurrentUserInitiator { get; init; }
    public string Message { get; init; } = string.Empty;
}
