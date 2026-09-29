using RoomTalk.Models;

namespace RoomTalk.Services;

public interface IRoomTalkServerService : IDisposable
{
    bool IsRunning { get; }
    int Port { get; }
    string StatusText { get; }
    IReadOnlyList<ConnectedClientInfo> ConnectedClients { get; }
    IReadOnlyList<ServerAccountItem> Accounts { get; }

    event EventHandler? StatusChanged;

    bool IsServerAccountUsername(string username);
    bool AuthenticateServerAccount(string username, string password);
    void SaveAccount(string username, string? password, AccountRole role, bool enabled);
    void DeleteAccount(string username);
    Task StartAsync(int port, CancellationToken cancellationToken = default);
    Task StopAsync();
}
