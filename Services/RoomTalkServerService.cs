using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using RoomTalk.Models;
using RoomTalk.Network;

namespace RoomTalk.Services;

public sealed class RoomTalkServerService : IRoomTalkServerService
{
    private static readonly TimeSpan CallSegmentDuration = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ExtensionConfirmationDuration = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<Guid, ServerClientSession> _clients = new();
    private readonly ConcurrentDictionary<Guid, ActiveCall> _calls = new();
    private readonly object _callSync = new();
    private readonly ServerConfigurationStore _configuration = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _serverCancellation;
    private Task? _acceptTask;
    private Task? _callTimerTask;
    private bool _disposed;
    private string _statusText = "Máy chủ chưa chạy.";

    public bool IsRunning => _listener is not null;
    public int Port { get; private set; }
    public string StatusText => _statusText;
    public IReadOnlyList<ServerAccountItem> Accounts => _configuration.GetAccountItems();

    public IReadOnlyList<ConnectedClientInfo> ConnectedClients => _clients.Values
        .Select(session => new ConnectedClientInfo
        {
            Username = session.Username,
            MachineName = session.MachineName,
            IpAddress = session.IpAddress,
            Role = session.Role,
            ConnectedAt = session.ConnectedAt
        })
        .OrderBy(item => item.Role)
        .ThenBy(item => item.Username, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public event EventHandler? StatusChanged;

    public RoomTalkServerService()
    {
        Port = _configuration.Port;
    }

    public bool IsServerAccountUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return false;
        }

        string normalizedUsername = username.Trim();
        return Accounts.Any(account =>
            account.Role == AccountRole.Server &&
            account.Username.Equals(normalizedUsername, StringComparison.OrdinalIgnoreCase));
    }

    public bool AuthenticateServerAccount(string username, string password)
    {
        ServerAccountRecord? account = _configuration.Authenticate(username, password);
        return account?.Role == AccountRole.Server;
    }

    public void SaveAccount(
        string username,
        string? password,
        AccountRole role,
        bool enabled)
    {
        _configuration.UpsertAccount(username, password, role, enabled);
        RaiseStatusChanged();
    }

    public void DeleteAccount(string username)
    {
        _configuration.DeleteAccount(username);
        RaiseStatusChanged();
    }

    public Task StartAsync(int port, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        Port = port;
        _configuration.Port = port;
        _serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _statusText = $"Máy chủ đang chạy tại cổng TCP {port}.";
        _acceptTask = AcceptClientsAsync(_serverCancellation.Token);
        _callTimerTask = MonitorCallTimersAsync(_serverCancellation.Token);
        RaiseStatusChanged();
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        TcpListener? listener = _listener;
        CancellationTokenSource? cancellation = _serverCancellation;
        Task? acceptTask = _acceptTask;
        Task? callTimerTask = _callTimerTask;
        _listener = null;
        _serverCancellation = null;
        _acceptTask = null;
        _callTimerTask = null;

        if (listener is null)
        {
            return;
        }

        try
        {
            cancellation?.Cancel();
            listener.Stop();
        }
        catch
        {
            // Đã dừng.
        }

        foreach (ServerClientSession session in _clients.Values)
        {
            session.Close();
        }

        _clients.Clear();
        lock (_callSync)
        {
            _calls.Clear();
        }

        foreach (Task? backgroundTask in new[] { acceptTask, callTimerTask })
        {
            if (backgroundTask is null)
            {
                continue;
            }

            try
            {
                await backgroundTask;
            }
            catch
            {
                // Tác vụ nền đã dừng.
            }
        }

        cancellation?.Dispose();
        _statusText = "Máy chủ đã dừng.";
        RaiseStatusChanged();
    }

    private async Task AcceptClientsAsync(CancellationToken cancellationToken)
    {
        TcpListener listener = _listener
            ?? throw new InvalidOperationException("Máy chủ chưa được khởi tạo.");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient tcpClient = await listener.AcceptTcpClientAsync(cancellationToken);
                tcpClient.NoDelay = true;
                _ = HandleClientAsync(tcpClient, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Dừng bình thường.
        }
        catch (ObjectDisposedException)
        {
            // Dừng bình thường.
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
            // Dừng bình thường.
        }
        catch (Exception exception)
        {
            _statusText = $"Máy chủ gặp lỗi: {exception.Message}";
            RaiseStatusChanged();
        }
    }

    private async Task MonitorCallTimersAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                DateTime nowUtc = DateTime.UtcNow;
                foreach (ActiveCall call in _calls.Values.ToArray())
                {
                    bool shouldEnd = false;
                    bool shouldBroadcast = false;
                    bool extensionRequired = false;
                    int secondsRemaining = 0;
                    string message = string.Empty;

                    lock (call.SyncRoot)
                    {
                        if (call.IsWaitingForExtension)
                        {
                            DateTime confirmationDeadline = call.ConfirmationExpiresAtUtc ?? nowUtc;
                            if (nowUtc >= confirmationDeadline)
                            {
                                shouldEnd = true;
                            }
                            else
                            {
                                extensionRequired = true;
                                secondsRemaining = RemainingWholeSeconds(confirmationDeadline, nowUtc);
                                message = "Nếu không chọn Tiếp tục trong 10 giây, phiên sẽ tự động kết thúc.";
                            }
                        }
                        else if (nowUtc >= call.SegmentExpiresAtUtc)
                        {
                            call.IsWaitingForExtension = true;
                            call.ConfirmationExpiresAtUtc = nowUtc.Add(ExtensionConfirmationDuration);
                            extensionRequired = true;
                            secondsRemaining = (int)ExtensionConfirmationDuration.TotalSeconds;
                            message = "Phiên đã đủ 1 phút. Nếu không chọn Tiếp tục trong 10 giây, phiên sẽ tự động kết thúc.";
                        }
                        else
                        {
                            secondsRemaining = RemainingWholeSeconds(call.SegmentExpiresAtUtc, nowUtc);
                        }

                        if (!shouldEnd &&
                            (secondsRemaining != call.LastBroadcastSeconds ||
                             extensionRequired != call.LastBroadcastWasExtensionWarning))
                        {
                            call.LastBroadcastSeconds = secondsRemaining;
                            call.LastBroadcastWasExtensionWarning = extensionRequired;
                            shouldBroadcast = true;
                        }
                    }

                    if (shouldEnd)
                    {
                        if (TryRemoveCall(call))
                        {
                            await SendCallStateAsync(
                                call,
                                false,
                                "Phiên đã tự kết thúc vì không được gia hạn trong 10 giây.",
                                cancellationToken);
                            await BroadcastAccountStatesAsync(cancellationToken);
                            UpdateServerStatus();
                        }

                        continue;
                    }

                    if (shouldBroadcast)
                    {
                        await SendCallTimerAsync(
                            call,
                            secondsRemaining,
                            extensionRequired,
                            message,
                            cancellationToken);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Dừng bình thường.
        }
        catch (ObjectDisposedException)
        {
            // Dừng bình thường.
        }
        catch (Exception exception)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _statusText = $"Bộ đếm phiên liên lạc gặp lỗi: {exception.Message}";
                RaiseStatusChanged();
            }
        }
    }

    private async Task HandleClientAsync(TcpClient tcpClient, CancellationToken serverCancellation)
    {
        ServerClientSession? session = null;
        try
        {
            await using var connection = new FramedConnection(tcpClient.GetStream());
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation);
            (WireFrameType Type, byte[] Payload)? loginFrame =
                await connection.ReadFrameAsync(linkedCancellation.Token);

            if (loginFrame is null || loginFrame.Value.Type != WireFrameType.Control)
            {
                return;
            }

            ControlMessage login = FramedConnection.DeserializeControl(loginFrame.Value.Payload);
            if (!login.Type.Equals("Login", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ServerAccountRecord? account = _configuration.Authenticate(login.Username, login.Password);
            if (account is null || account.Role == AccountRole.Server)
            {
                await connection.SendControlAsync(new ControlMessage
                {
                    Type = "LoginResult",
                    Success = false,
                    Message = "Sai tài khoản, mật khẩu hoặc tài khoản không được phép đăng nhập từ máy trạm."
                }, linkedCancellation.Token);
                return;
            }

            if (_clients.Values.Any(item =>
                    item.Username.Equals(account.Username, StringComparison.OrdinalIgnoreCase)))
            {
                await connection.SendControlAsync(new ControlMessage
                {
                    Type = "LoginResult",
                    Success = false,
                    Message = "Tài khoản này đang được sử dụng trên một máy khác."
                }, linkedCancellation.Token);
                return;
            }

            Guid clientId = Guid.NewGuid();
            string ipAddress = (tcpClient.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString()
                               ?? "Không xác định";
            session = new ServerClientSession(
                clientId,
                account.Username,
                login.MachineName,
                ipAddress,
                account.Role,
                tcpClient,
                connection,
                linkedCancellation);

            if (!_clients.TryAdd(clientId, session))
            {
                return;
            }

            string[] contactNames = account.Role == AccountRole.Admin
                ? _configuration.GetCommunicationAccounts()
                    .Where(item => item.Enabled && item.Role == AccountRole.User)
                    .Select(item => item.Username)
                    .ToArray()
                : [];

            string loginMessage = account.Role == AccountRole.Admin
                ? "Đã đăng nhập. Chọn tài khoản người dùng đang online để bắt đầu nói."
                : "Đã đăng nhập. Thiết bị đang chờ tài khoản điều hành liên lạc.";

            await session.SendControlAsync(new ControlMessage
            {
                Type = "LoginResult",
                Success = true,
                ClientId = clientId,
                Username = account.Username,
                Role = account.Role.ToString(),
                Rooms = contactNames,
                Message = loginMessage
            }, linkedCancellation.Token);

            UpdateServerStatus();
            await BroadcastAccountStatesAsync(serverCancellation);

            while (!linkedCancellation.IsCancellationRequested)
            {
                (WireFrameType Type, byte[] Payload)? frame =
                    await connection.ReadFrameAsync(linkedCancellation.Token);
                if (frame is null)
                {
                    break;
                }

                if (frame.Value.Type == WireFrameType.Control)
                {
                    await HandleControlMessageAsync(
                        session,
                        FramedConnection.DeserializeControl(frame.Value.Payload),
                        serverCancellation);
                }
                else if (frame.Value.Type == WireFrameType.Audio)
                {
                    await ForwardAudioAsync(session, frame.Value.Payload, serverCancellation);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Kết nối đóng.
        }
        catch (IOException)
        {
            // Máy trạm ngắt.
        }
        catch (SocketException)
        {
            // Máy trạm ngắt.
        }
        catch
        {
            // Một client lỗi không được làm dừng server.
        }
        finally
        {
            if (session is not null)
            {
                _clients.TryRemove(session.ClientId, out _);
                try
                {
                    await EndCallsForClientAsync(session.ClientId, "Một bên đã mất kết nối.", serverCancellation);
                }
                catch
                {
                    // Máy chủ có thể đang dừng.
                }

                session.Close();
                UpdateServerStatus();
                if (IsRunning)
                {
                    try
                    {
                        await BroadcastAccountStatesAsync(serverCancellation);
                    }
                    catch
                    {
                        // Server đang dừng.
                    }
                }
            }
            else
            {
                tcpClient.Dispose();
            }
        }
    }

    private async Task HandleControlMessageAsync(
        ServerClientSession session,
        ControlMessage message,
        CancellationToken cancellationToken)
    {
        if (message.Type.Equals("StartCall", StringComparison.OrdinalIgnoreCase))
        {
            await StartCallAsync(session, message, cancellationToken);
            return;
        }

        if (message.Type.Equals("StopCall", StringComparison.OrdinalIgnoreCase))
        {
            await StopCallAsync(session, cancellationToken);
            return;
        }

        if (message.Type.Equals("ExtendCall", StringComparison.OrdinalIgnoreCase))
        {
            await ExtendCallAsync(session, message, cancellationToken);
            return;
        }

        if (message.Type.Equals("Ping", StringComparison.OrdinalIgnoreCase))
        {
            await session.SendControlAsync(new ControlMessage
            {
                Type = "Pong",
                RequestId = message.RequestId,
                Success = true
            }, cancellationToken);
        }
    }

    private async Task StartCallAsync(
        ServerClientSession initiator,
        ControlMessage request,
        CancellationToken cancellationToken)
    {
        if (initiator.Role != AccountRole.Admin)
        {
            await SendRequestResultAsync(
                initiator,
                request,
                false,
                "Tài khoản người dùng chỉ có chế độ nghe và không được bắt đầu liên lạc.",
                cancellationToken);
            return;
        }

        string targetUsername = request.RoomName.Trim();
        if (string.IsNullOrWhiteSpace(targetUsername))
        {
            await SendRequestResultAsync(initiator, request, false, "Hãy chọn tài khoản cần liên lạc.", cancellationToken);
            return;
        }

        if (initiator.Username.Equals(targetUsername, StringComparison.OrdinalIgnoreCase))
        {
            await SendRequestResultAsync(initiator, request, false, "Không thể gọi chính tài khoản đang đăng nhập.", cancellationToken);
            return;
        }

        ServerAccountRecord? targetAccount = _configuration.FindAccount(targetUsername);
        if (targetAccount is null || !targetAccount.Enabled || targetAccount.Role != AccountRole.User)
        {
            await SendRequestResultAsync(
                initiator,
                request,
                false,
                "Chỉ có thể truyền âm thanh tới tài khoản người dùng đang hoạt động.",
                cancellationToken);
            return;
        }

        ServerClientSession? recipient = _clients.Values.FirstOrDefault(client =>
            client.Username.Equals(targetUsername, StringComparison.OrdinalIgnoreCase));
        if (recipient is null)
        {
            await SendRequestResultAsync(initiator, request, false, "Tài khoản này đang offline.", cancellationToken);
            return;
        }

        if (recipient.Role != AccountRole.User)
        {
            await SendRequestResultAsync(
                initiator,
                request,
                false,
                "Tài khoản đích không ở chế độ người dùng. Hãy yêu cầu đăng nhập lại sau khi đổi vai trò.",
                cancellationToken);
            return;
        }

        ActiveCall? call = null;
        string? error = null;
        lock (_callSync)
        {
            if (FindCallForClientUnsafe(initiator.ClientId) is not null)
            {
                error = "Tài khoản của bạn đang trong một cuộc liên lạc khác.";
            }
            else if (FindCallForClientUnsafe(recipient.ClientId) is not null)
            {
                error = "Tài khoản cần gọi đang bận.";
            }
            else
            {
                DateTime nowUtc = DateTime.UtcNow;
                call = new ActiveCall(
                    Guid.NewGuid(),
                    initiator.ClientId,
                    recipient.ClientId,
                    initiator.Username,
                    recipient.Username,
                    nowUtc.Add(CallSegmentDuration));
                _calls[call.CallId] = call;
            }
        }

        if (call is null)
        {
            await SendRequestResultAsync(
                initiator,
                request,
                false,
                error ?? "Không thể mở phiên liên lạc.",
                cancellationToken);
            return;
        }

        await SendRequestResultAsync(initiator, request, true, "Đã bắt đầu truyền âm thanh trong 1 phút.", cancellationToken);
        await SendCallStateAsync(call, true, "Máy điều hành đã bắt đầu truyền âm thanh.", cancellationToken);
        await SendCallTimerAsync(call, (int)CallSegmentDuration.TotalSeconds, false, string.Empty, cancellationToken);
        await BroadcastAccountStatesAsync(cancellationToken);
        UpdateServerStatus();
    }

    private async Task StopCallAsync(
        ServerClientSession requestingSession,
        CancellationToken cancellationToken)
    {
        ActiveCall? call = FindCallForClient(requestingSession.ClientId);
        if (call is null)
        {
            return;
        }

        if (TryRemoveCall(call))
        {
            string message = call.InitiatorClientId == requestingSession.ClientId
                ? $"{call.InitiatorUsername} đã kết thúc liên lạc."
                : $"{call.RecipientUsername} đã kết thúc liên lạc.";
            await SendCallStateAsync(call, false, message, cancellationToken);
            await BroadcastAccountStatesAsync(cancellationToken);
            UpdateServerStatus();
        }
    }

    private async Task ExtendCallAsync(
        ServerClientSession requestingSession,
        ControlMessage request,
        CancellationToken cancellationToken)
    {
        ActiveCall? call = FindCallForClient(requestingSession.ClientId);
        if (call is null || call.InitiatorClientId != requestingSession.ClientId)
        {
            await SendRequestResultAsync(
                requestingSession,
                request,
                false,
                "Chỉ tài khoản đã bắt đầu cuộc liên lạc mới được gia hạn.",
                cancellationToken);
            return;
        }

        bool expired;
        lock (call.SyncRoot)
        {
            DateTime nowUtc = DateTime.UtcNow;
            expired = !call.IsWaitingForExtension ||
                      call.ConfirmationExpiresAtUtc is null ||
                      nowUtc >= call.ConfirmationExpiresAtUtc.Value;

            if (!expired)
            {
                call.IsWaitingForExtension = false;
                call.ConfirmationExpiresAtUtc = null;
                call.SegmentExpiresAtUtc = nowUtc.Add(CallSegmentDuration);
                call.LastBroadcastSeconds = (int)CallSegmentDuration.TotalSeconds;
                call.LastBroadcastWasExtensionWarning = false;
            }
        }

        if (expired)
        {
            await SendRequestResultAsync(
                requestingSession,
                request,
                false,
                "Đã hết thời gian 10 giây hoặc phiên chưa đến lúc gia hạn.",
                cancellationToken);
            return;
        }

        await SendRequestResultAsync(requestingSession, request, true, "Đã tiếp tục liên lạc thêm 1 phút.", cancellationToken);
        await SendCallTimerAsync(
            call,
            (int)CallSegmentDuration.TotalSeconds,
            false,
            "Đã tiếp tục liên lạc thêm 1 phút.",
            cancellationToken,
            "CallExtended");
    }

    private async Task ForwardAudioAsync(
        ServerClientSession sender,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        if (payload.Length == 0)
        {
            return;
        }

        ActiveCall? call = FindCallForClient(sender.ClientId);
        if (call is null)
        {
            return;
        }

        // Chỉ tài khoản Admin đã mở phiên mới được gửi âm thanh.
        // Audio từ tài khoản User bị bỏ ở máy chủ để bảo đảm chế độ chỉ nghe.
        if (sender.Role != AccountRole.Admin || sender.ClientId != call.InitiatorClientId)
        {
            return;
        }

        Guid recipientId = call.RecipientClientId;
        if (_clients.TryGetValue(recipientId, out ServerClientSession? recipient))
        {
            try
            {
                await recipient.SendAudioAsync(payload, cancellationToken);
            }
            catch
            {
                recipient.Close();
            }
        }
    }

    private async Task EndCallsForClientAsync(
        Guid clientId,
        string reason,
        CancellationToken cancellationToken)
    {
        ActiveCall[] calls = _calls.Values
            .Where(call => call.ContainsClient(clientId))
            .ToArray();

        foreach (ActiveCall call in calls)
        {
            if (TryRemoveCall(call))
            {
                await SendCallStateAsync(call, false, reason, cancellationToken);
            }
        }
    }

    private async Task SendCallStateAsync(
        ActiveCall call,
        bool isActive,
        string message,
        CancellationToken cancellationToken)
    {
        await SendPersonalizedCallControlAsync(
            call,
            (peerUsername, isInitiator) => new ControlMessage
            {
                Type = isActive ? "CallStarted" : "CallEnded",
                Success = true,
                CallId = call.CallId,
                IsSessionActive = isActive,
                RoomName = peerUsername,
                InitiatorUsername = call.InitiatorUsername,
                RecipientUsername = call.RecipientUsername,
                IsCallInitiator = isInitiator,
                Message = message
            },
            cancellationToken);
    }

    private async Task SendCallTimerAsync(
        ActiveCall call,
        int secondsRemaining,
        bool extensionRequired,
        string message,
        CancellationToken cancellationToken,
        string? messageType = null)
    {
        await SendPersonalizedCallControlAsync(
            call,
            (peerUsername, isInitiator) => new ControlMessage
            {
                Type = messageType ?? (extensionRequired ? "CallExtensionRequired" : "CallTimer"),
                Success = true,
                CallId = call.CallId,
                IsSessionActive = true,
                RoomName = peerUsername,
                InitiatorUsername = call.InitiatorUsername,
                RecipientUsername = call.RecipientUsername,
                IsCallInitiator = isInitiator,
                SecondsRemaining = Math.Max(0, secondsRemaining),
                IsExtensionConfirmationRequired = extensionRequired,
                Message = message
            },
            cancellationToken);
    }

    private async Task SendPersonalizedCallControlAsync(
        ActiveCall call,
        Func<string, bool, ControlMessage> messageFactory,
        CancellationToken cancellationToken)
    {
        if (_clients.TryGetValue(call.InitiatorClientId, out ServerClientSession? initiator))
        {
            try
            {
                await initiator.SendControlAsync(
                    messageFactory(call.RecipientUsername, true),
                    cancellationToken);
            }
            catch
            {
                initiator.Close();
            }
        }

        if (_clients.TryGetValue(call.RecipientClientId, out ServerClientSession? recipient))
        {
            try
            {
                await recipient.SendControlAsync(
                    messageFactory(call.InitiatorUsername, false),
                    cancellationToken);
            }
            catch
            {
                recipient.Close();
            }
        }
    }

    private async Task BroadcastAccountStatesAsync(CancellationToken cancellationToken)
    {
        ServerAccountRecord[] accounts = _configuration.GetCommunicationAccounts()
            .Where(account => account.Enabled && account.Role == AccountRole.User)
            .ToArray();
        ServerClientSession[] clients = _clients.Values.ToArray();
        ActiveCall[] calls = _calls.Values.ToArray();

        foreach (ServerAccountRecord account in accounts)
        {
            ServerClientSession? onlineClient = clients.FirstOrDefault(client =>
                client.Username.Equals(account.Username, StringComparison.OrdinalIgnoreCase));
            ActiveCall? call = onlineClient is null
                ? null
                : calls.FirstOrDefault(item => item.ContainsClient(onlineClient.ClientId));

            string peerUsername = call is null
                ? string.Empty
                : call.GetPeerUsername(account.Username);

            var state = new ControlMessage
            {
                Type = "RoomState",
                Success = true,
                RoomName = account.Username,
                OnlineClients = onlineClient is null ? 0 : 1,
                ConnectedUser = account.Username,
                IsSessionActive = call is not null,
                ActiveOperator = peerUsername
            };

            foreach (ServerClientSession recipient in clients)
            {
                try
                {
                    await recipient.SendControlAsync(state, cancellationToken);
                }
                catch
                {
                    recipient.Close();
                }
            }
        }
    }

    private static Task SendRequestResultAsync(
        ServerClientSession session,
        ControlMessage request,
        bool success,
        string message,
        CancellationToken cancellationToken)
    {
        return session.SendControlAsync(new ControlMessage
        {
            Type = success ? "RequestGranted" : "RequestDenied",
            RequestId = request.RequestId,
            Success = success,
            RoomName = request.RoomName,
            Message = message
        }, cancellationToken);
    }

    private ActiveCall? FindCallForClient(Guid clientId)
    {
        lock (_callSync)
        {
            return FindCallForClientUnsafe(clientId);
        }
    }

    private ActiveCall? FindCallForClientUnsafe(Guid clientId)
    {
        return _calls.Values.FirstOrDefault(call => call.ContainsClient(clientId));
    }

    private bool TryRemoveCall(ActiveCall call)
    {
        lock (_callSync)
        {
            return _calls.TryRemove(call.CallId, out ActiveCall? removed) &&
                   ReferenceEquals(removed, call);
        }
    }

    private static int RemainingWholeSeconds(DateTime deadlineUtc, DateTime nowUtc)
    {
        return Math.Max(0, (int)Math.Ceiling((deadlineUtc - nowUtc).TotalSeconds));
    }

    private void UpdateServerStatus()
    {
        _statusText = IsRunning
            ? $"Máy chủ đang chạy · {_clients.Count} máy kết nối · {_calls.Count} cuộc liên lạc."
            : "Máy chủ đã dừng.";
        RaiseStatusChanged();
    }

    private void RaiseStatusChanged() => StatusChanged?.Invoke(this, EventArgs.Empty);

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(RoomTalkServerService));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopAsync().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    private sealed class ActiveCall
    {
        public ActiveCall(
            Guid callId,
            Guid initiatorClientId,
            Guid recipientClientId,
            string initiatorUsername,
            string recipientUsername,
            DateTime segmentExpiresAtUtc)
        {
            CallId = callId;
            InitiatorClientId = initiatorClientId;
            RecipientClientId = recipientClientId;
            InitiatorUsername = initiatorUsername;
            RecipientUsername = recipientUsername;
            SegmentExpiresAtUtc = segmentExpiresAtUtc;
            LastBroadcastSeconds = (int)CallSegmentDuration.TotalSeconds;
        }

        public object SyncRoot { get; } = new();
        public Guid CallId { get; }
        public Guid InitiatorClientId { get; }
        public Guid RecipientClientId { get; }
        public string InitiatorUsername { get; }
        public string RecipientUsername { get; }
        public DateTime SegmentExpiresAtUtc { get; set; }
        public DateTime? ConfirmationExpiresAtUtc { get; set; }
        public bool IsWaitingForExtension { get; set; }
        public int LastBroadcastSeconds { get; set; } = -1;
        public bool LastBroadcastWasExtensionWarning { get; set; }

        public bool ContainsClient(Guid clientId) =>
            InitiatorClientId == clientId || RecipientClientId == clientId;

        public string GetPeerUsername(string username) =>
            InitiatorUsername.Equals(username, StringComparison.OrdinalIgnoreCase)
                ? RecipientUsername
                : InitiatorUsername;
    }

    private sealed class ServerClientSession
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private int _closed;

        public ServerClientSession(
            Guid clientId,
            string username,
            string machineName,
            string ipAddress,
            AccountRole role,
            TcpClient tcpClient,
            FramedConnection connection,
            CancellationTokenSource cancellation)
        {
            ClientId = clientId;
            Username = username;
            MachineName = string.IsNullOrWhiteSpace(machineName) ? "Không xác định" : machineName;
            IpAddress = ipAddress;
            Role = role;
            TcpClient = tcpClient;
            Connection = connection;
            Cancellation = cancellation;
            ConnectedAt = DateTime.Now;
        }

        public Guid ClientId { get; }
        public string Username { get; }
        public string MachineName { get; }
        public string IpAddress { get; }
        public AccountRole Role { get; }
        public DateTime ConnectedAt { get; }
        public TcpClient TcpClient { get; }
        public FramedConnection Connection { get; }
        public CancellationTokenSource Cancellation { get; }

        public async Task SendControlAsync(ControlMessage message, CancellationToken cancellationToken)
        {
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                await Connection.SendControlAsync(message, cancellationToken);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async Task SendAudioAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                await Connection.SendAudioAsync(data, cancellationToken);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return;
            }

            try { Cancellation.Cancel(); } catch { }
            try { TcpClient.Close(); } catch { }
        }
    }
}
