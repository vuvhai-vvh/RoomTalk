using RoomTalk.Models;

namespace RoomTalk.Services;

public interface IRoomTalkClientService : IAsyncDisposable
{
    bool IsConnected { get; }
    string Username { get; }
    AccountRole Role { get; }

    event Action<RoomRuntimeState>? RoomStateChanged;
    event Action<CallSessionState>? CallSessionChanged;
    event Action<CallTimerState>? CallTimerChanged;
    event Action<byte[]>? AudioFrameReceived;
    event Action<string>? ConnectionLost;

    Task<LoginSessionResult> ConnectAsync(
        string serverAddress,
        int port,
        string username,
        string password,
        CancellationToken cancellationToken = default);

    Task DisconnectAsync();
    Task<(bool Granted, string Message)> StartCallAsync(
        string accountName,
        CancellationToken cancellationToken = default);
    Task<(bool Granted, string Message)> ExtendCallAsync(
        string accountName,
        CancellationToken cancellationToken = default);
    Task StopCallAsync(string accountName, CancellationToken cancellationToken = default);
    Task SendAudioFrameAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default);
}
