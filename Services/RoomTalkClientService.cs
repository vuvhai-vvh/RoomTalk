using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using RoomTalk.Models;
using RoomTalk.Network;

namespace RoomTalk.Services;

public sealed class RoomTalkClientService : IRoomTalkClientService
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<ControlMessage>> _pendingRequests = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private TcpClient? _tcpClient;
    private FramedConnection? _connection;
    private CancellationTokenSource? _connectionCancellation;
    private Task? _receiveTask;
    private bool _disposed;

    public bool IsConnected => _tcpClient?.Connected == true && _connection is not null;
    public string Username { get; private set; } = string.Empty;
    public Guid ClientId { get; private set; }
    public AccountRole Role { get; private set; } = AccountRole.User;

    public event Action<RoomRuntimeState>? RoomStateChanged;
    public event Action<CallSessionState>? CallSessionChanged;
    public event Action<CallTimerState>? CallTimerChanged;
    public event Action<byte[]>? AudioFrameReceived;
    public event Action<string>? ConnectionLost;

    public async Task<LoginSessionResult> ConnectAsync(
        string serverAddress,
        int port,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await DisconnectAsync();

        var tcpClient = new TcpClient { NoDelay = true };
        try
        {
            using var loginTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            loginTimeout.CancelAfter(TimeSpan.FromSeconds(8));
            await tcpClient.ConnectAsync(serverAddress, port, loginTimeout.Token);
            var connection = new FramedConnection(tcpClient.GetStream());

            await connection.SendControlAsync(new ControlMessage
            {
                Type = "Login",
                Username = username,
                Password = password,
                MachineName = Environment.MachineName
            }, loginTimeout.Token);

            (WireFrameType Type, byte[] Payload)? frame = await connection.ReadFrameAsync(loginTimeout.Token);
            if (frame is null || frame.Value.Type != WireFrameType.Control)
            {
                await connection.DisposeAsync();
                tcpClient.Dispose();
                return LoginSessionResult.Failure("Máy chủ không trả về kết quả đăng nhập hợp lệ.");
            }

            ControlMessage response = FramedConnection.DeserializeControl(frame.Value.Payload);
            if (!response.Type.Equals("LoginResult", StringComparison.OrdinalIgnoreCase) || !response.Success)
            {
                await connection.DisposeAsync();
                tcpClient.Dispose();
                return LoginSessionResult.Failure(
                    string.IsNullOrWhiteSpace(response.Message)
                        ? "Máy chủ từ chối đăng nhập."
                        : response.Message);
            }

            if (!Enum.TryParse(response.Role, true, out AccountRole role) || role == AccountRole.Server)
            {
                await connection.DisposeAsync();
                tcpClient.Dispose();
                return LoginSessionResult.Failure("Vai trò tài khoản không hợp lệ.");
            }

            _tcpClient = tcpClient;
            _connection = connection;
            _connectionCancellation = new CancellationTokenSource();
            ClientId = response.ClientId;
            Username = string.IsNullOrWhiteSpace(response.Username)
                ? username.Trim()
                : response.Username;
            Role = role;
            _receiveTask = ReceiveLoopAsync(_connectionCancellation.Token);

            return new LoginSessionResult
            {
                Success = true,
                Role = role,
                Username = Username,
                Message = response.Message,
                Rooms = response.Rooms
            };
        }
        catch (OperationCanceledException)
        {
            tcpClient.Dispose();
            return LoginSessionResult.Failure("Không kết nối được trong 8 giây hoặc thao tác đã bị hủy.");
        }
        catch (SocketException exception)
        {
            tcpClient.Dispose();
            return LoginSessionResult.Failure($"Không kết nối được tới {serverAddress}:{port}. {exception.Message}");
        }
        catch (Exception exception)
        {
            tcpClient.Dispose();
            return LoginSessionResult.Failure($"Không thể đăng nhập: {exception.Message}");
        }
    }

    public async Task DisconnectAsync()
    {
        FramedConnection? connection = _connection;
        TcpClient? tcpClient = _tcpClient;
        CancellationTokenSource? cancellation = _connectionCancellation;
        Task? receiveTask = _receiveTask;

        _connection = null;
        _tcpClient = null;
        _connectionCancellation = null;
        _receiveTask = null;
        ClientId = Guid.Empty;
        Username = string.Empty;
        Role = AccountRole.User;

        if (connection is null && tcpClient is null)
        {
            return;
        }

        try
        {
            cancellation?.Cancel();
            tcpClient?.Close();
        }
        catch
        {
            // Kết nối đã đóng.
        }

        if (receiveTask is not null)
        {
            try { await Task.WhenAny(receiveTask, Task.Delay(1000)); } catch { }
        }

        if (connection is not null)
        {
            try { await connection.DisposeAsync(); } catch { }
        }

        cancellation?.Dispose();
        tcpClient?.Dispose();
        foreach (TaskCompletionSource<ControlMessage> pending in _pendingRequests.Values)
        {
            pending.TrySetCanceled();
        }
        _pendingRequests.Clear();
    }

    public Task<(bool Granted, string Message)> StartCallAsync(
        string accountName,
        CancellationToken cancellationToken = default) =>
        SendRequestAsync("StartCall", accountName, cancellationToken);

    public Task<(bool Granted, string Message)> ExtendCallAsync(
        string accountName,
        CancellationToken cancellationToken = default) =>
        SendRequestAsync("ExtendCall", accountName, cancellationToken);

    public Task StopCallAsync(string accountName, CancellationToken cancellationToken = default)
    {
        return SendControlSafeAsync(new ControlMessage
        {
            Type = "StopCall",
            ClientId = ClientId,
            RoomName = accountName
        }, cancellationToken);
    }

    public Task SendAudioFrameAsync(
        ReadOnlyMemory<byte> audioData,
        CancellationToken cancellationToken = default)
    {
        return SendAudioSafeAsync(audioData, cancellationToken);
    }

    private async Task<(bool Granted, string Message)> SendRequestAsync(
        string type,
        string accountName,
        CancellationToken cancellationToken)
    {
        Guid requestId = Guid.NewGuid();
        var completion = new TaskCompletionSource<ControlMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingRequests.TryAdd(requestId, completion))
        {
            return (false, "Không thể tạo yêu cầu.");
        }

        try
        {
            await SendControlSafeAsync(new ControlMessage
            {
                Type = type,
                RequestId = requestId,
                ClientId = ClientId,
                RoomName = accountName
            }, cancellationToken);

            ControlMessage response = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            return (response.Success, response.Message);
        }
        catch (TimeoutException)
        {
            return (false, "Máy chủ không phản hồi yêu cầu.");
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        FramedConnection connection = GetConnection();
        string disconnectReason = "Đã mất kết nối tới máy chủ RoomTalk.";

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                (WireFrameType Type, byte[] Payload)? frame =
                    await connection.ReadFrameAsync(cancellationToken);
                if (frame is null)
                {
                    break;
                }

                if (frame.Value.Type == WireFrameType.Audio)
                {
                    AudioFrameReceived?.Invoke(frame.Value.Payload);
                    continue;
                }

                ControlMessage message = FramedConnection.DeserializeControl(frame.Value.Payload);
                if (message.RequestId != Guid.Empty &&
                    _pendingRequests.TryGetValue(message.RequestId, out TaskCompletionSource<ControlMessage>? pending))
                {
                    pending.TrySetResult(message);
                }

                if (message.Type.Equals("RoomState", StringComparison.OrdinalIgnoreCase))
                {
                    RoomStateChanged?.Invoke(new RoomRuntimeState
                    {
                        RoomName = message.RoomName,
                        OnlineClients = message.OnlineClients,
                        IsSessionActive = message.IsSessionActive,
                        ActiveOperator = message.ActiveOperator,
                        ConnectedUser = message.ConnectedUser
                    });
                }
                else if (message.Type.Equals("CallStarted", StringComparison.OrdinalIgnoreCase) ||
                         message.Type.Equals("CallEnded", StringComparison.OrdinalIgnoreCase))
                {
                    CallSessionChanged?.Invoke(new CallSessionState
                    {
                        CallId = message.CallId,
                        IsActive = message.IsSessionActive,
                        RoomName = message.RoomName,
                        InitiatorUsername = message.InitiatorUsername,
                        RecipientUsername = message.RecipientUsername,
                        IsCurrentUserInitiator = message.IsCallInitiator,
                        Message = message.Message
                    });
                }
                else if (message.Type.Equals("CallTimer", StringComparison.OrdinalIgnoreCase) ||
                         message.Type.Equals("CallExtensionRequired", StringComparison.OrdinalIgnoreCase) ||
                         message.Type.Equals("CallExtended", StringComparison.OrdinalIgnoreCase))
                {
                    CallTimerChanged?.Invoke(new CallTimerState
                    {
                        CallId = message.CallId,
                        RoomName = message.RoomName,
                        SecondsRemaining = message.SecondsRemaining,
                        IsExtensionConfirmationRequired = message.IsExtensionConfirmationRequired,
                        IsCurrentUserInitiator = message.IsCallInitiator,
                        Message = message.Message
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (IOException exception)
        {
            disconnectReason = $"Kết nối tới máy chủ bị đóng: {exception.Message}";
        }
        catch (SocketException exception)
        {
            disconnectReason = $"Mất kết nối mạng: {exception.Message}";
        }
        catch (Exception exception)
        {
            disconnectReason = $"Kết nối RoomTalk gặp lỗi: {exception.Message}";
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            ConnectionLost?.Invoke(disconnectReason);
        }
    }

    private async Task SendControlSafeAsync(ControlMessage message, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await GetConnection().SendControlAsync(message, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendAudioSafeAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await GetConnection().SendAudioAsync(data, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private FramedConnection GetConnection()
    {
        ThrowIfDisposed();
        return _connection ?? throw new InvalidOperationException("Chưa kết nối tới máy chủ RoomTalk.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(RoomTalkClientService));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisconnectAsync();
        _sendLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
