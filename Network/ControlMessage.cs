namespace RoomTalk.Network;

internal sealed class ControlMessage
{
    public string Type { get; set; } = string.Empty;
    public Guid RequestId { get; set; }
    public Guid ClientId { get; set; }
    public Guid CallId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string RoomName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string[] Rooms { get; set; } = [];
    public int OnlineClients { get; set; }
    public bool IsSessionActive { get; set; }
    public string ActiveOperator { get; set; } = string.Empty;
    public string ConnectedUser { get; set; } = string.Empty;
    public string InitiatorUsername { get; set; } = string.Empty;
    public string RecipientUsername { get; set; } = string.Empty;
    public bool IsCallInitiator { get; set; }
    public int SecondsRemaining { get; set; }
    public bool IsExtensionConfirmationRequired { get; set; }
}
