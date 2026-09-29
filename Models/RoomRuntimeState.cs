namespace RoomTalk.Models;

public sealed class RoomRuntimeState
{
    public string RoomName { get; init; } = string.Empty;
    public int OnlineClients { get; init; }
    public bool IsSessionActive { get; init; }
    public string ActiveOperator { get; init; } = string.Empty;
    public string ConnectedUser { get; init; } = string.Empty;
    public bool IsBusy => IsSessionActive;
}
