using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoomTalk.Models;
using RoomTalk.Services;

namespace RoomTalk.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IWindowService _windowService;
    private readonly IRoomTalkClientService _clientService;
    private readonly IRoomTalkServerService _serverService;
    private readonly IAudioCommunicationService _audioCommunicationService;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly Dictionary<string, RoomRuntimeState> _roomStates = new(StringComparer.OrdinalIgnoreCase);

    private string _serverIp = "127.0.0.1";
    private string _serverPort = "5000";
    private string _username = string.Empty;
    private string _password = string.Empty;
    private bool _isConnected;
    private AccountRole _role = AccountRole.User;
    private string[] _allowedRooms = [];
    private string _connectionStatus = "Chưa kết nối";
    private string _accountStatus = "Chưa đăng nhập";
    private string _statusMessage = "Đăng nhập để sử dụng RoomTalk.";
    private string _selectedMicrophone = "Chưa chọn microphone";
    private string _selectedSpeaker = "Chưa chọn loa";
    private AudioSettingsData _audioSettings = new();
    private string _activeCallRoom = string.Empty;
    private Guid _activeCallId;
    private bool _isCallActive;
    private bool _isCurrentUserCallInitiator;
    private int _callSecondsRemaining;
    private bool _isExtensionPromptVisible;
    private bool _isExtensionActionBusy;
    private bool _isStartingAudio;
    private double _microphoneLevel;
    private bool _disposed;

    public MainViewModel(
        IWindowService windowService,
        IRoomTalkClientService clientService,
        IRoomTalkServerService serverService,
        IAudioCommunicationService audioCommunicationService,
        IAudioDeviceService audioDeviceService)
    {
        _windowService = windowService;
        _clientService = clientService;
        _serverService = serverService;
        _audioCommunicationService = audioCommunicationService;

        LoginCommand = new AsyncRelayCommand(LoginOrLogoutAsync);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        ContinueCallCommand = new AsyncRelayCommand(ContinueCallAsync, CanContinueCall);
        EndCurrentCallCommand = new AsyncRelayCommand(EndCurrentCallAsync, CanEndCurrentCall);

        _clientService.RoomStateChanged += ClientServiceOnRoomStateChanged;
        _clientService.CallSessionChanged += ClientServiceOnCallSessionChanged;
        _clientService.CallTimerChanged += ClientServiceOnCallTimerChanged;
        _clientService.ConnectionLost += ClientServiceOnConnectionLost;
        _audioCommunicationService.MicrophoneLevelChanged += AudioCommunicationServiceOnMicrophoneLevelChanged;

        _audioSettings = AudioSettingsStore.LoadOrDefault();
        _serverIp = _audioSettings.ServerAddress;
        _serverPort = _audioSettings.ServerPort.ToString();
        SelectDefaultAudioDevices(audioDeviceService);
        _audioCommunicationService.ApplySettings(_audioSettings);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(IsDisconnectedViewVisible));
                OnPropertyChanged(nameof(IsCommunicationViewVisible));
                OnPropertyChanged(nameof(LoginButtonText));
                OnPropertyChanged(nameof(IsSettingsVisible));
                OnPropertyChanged(nameof(IsAdminContactListVisible));
                OnPropertyChanged(nameof(IsUserListenerViewVisible));
                OnPropertyChanged(nameof(IsMicrophoneMeterVisible));
                OnPropertyChanged(nameof(IsUserEndCallVisible));
                OnPropertyChanged(nameof(CallStatusText));
                OnPropertyChanged(nameof(MicrophoneMeterText));
                OnPropertyChanged(nameof(IsInitiatorExtensionPromptVisible));
                OnPropertyChanged(nameof(IsRecipientEndingWarningVisible));
                ContinueCallCommand.NotifyCanExecuteChanged();
                EndCurrentCallCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public AccountRole Role
    {
        get => _role;
        private set
        {
            if (SetProperty(ref _role, value))
            {
                OnPropertyChanged(nameof(RoleText));
                OnPropertyChanged(nameof(IsAdminContactListVisible));
                OnPropertyChanged(nameof(IsUserListenerViewVisible));
                OnPropertyChanged(nameof(IsMicrophoneMeterVisible));
                OnPropertyChanged(nameof(IsUserEndCallVisible));
                OnPropertyChanged(nameof(CallStatusText));
                OnPropertyChanged(nameof(MicrophoneMeterText));
            }
        }
    }

    public bool IsDisconnectedViewVisible => !IsConnected;
    public bool IsCommunicationViewVisible => IsConnected;
    public bool IsAdminContactListVisible => IsConnected && Role == AccountRole.Admin;
    public bool IsUserListenerViewVisible => IsConnected && Role == AccountRole.User;
    public bool IsMicrophoneMeterVisible => IsConnected && Role == AccountRole.Admin;
    public bool IsUserEndCallVisible => IsConnected && Role == AccountRole.User && IsCallActive;
    public string RoleText => Role == AccountRole.Admin ? "Điều hành" : "Máy nghe";
    public string CurrentUsername => _username;

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => SetProperty(ref _connectionStatus, value);
    }

    public string AccountStatus
    {
        get => _accountStatus;
        private set => SetProperty(ref _accountStatus, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string SelectedMicrophone
    {
        get => _selectedMicrophone;
        private set => SetProperty(ref _selectedMicrophone, value);
    }

    public string SelectedSpeaker
    {
        get => _selectedSpeaker;
        private set => SetProperty(ref _selectedSpeaker, value);
    }

    public bool IsCallActive
    {
        get => _isCallActive;
        private set
        {
            if (SetProperty(ref _isCallActive, value))
            {
                OnPropertyChanged(nameof(CallStatusText));
                OnPropertyChanged(nameof(MicrophoneMeterText));
                OnPropertyChanged(nameof(CallCountdownText));
                OnPropertyChanged(nameof(IsInitiatorExtensionPromptVisible));
                OnPropertyChanged(nameof(IsRecipientEndingWarningVisible));
                OnPropertyChanged(nameof(IsUserEndCallVisible));
                ContinueCallCommand.NotifyCanExecuteChanged();
                EndCurrentCallCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsCurrentUserCallInitiator
    {
        get => _isCurrentUserCallInitiator;
        private set
        {
            if (SetProperty(ref _isCurrentUserCallInitiator, value))
            {
                OnPropertyChanged(nameof(IsInitiatorExtensionPromptVisible));
                OnPropertyChanged(nameof(IsRecipientEndingWarningVisible));
                ContinueCallCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string ActiveCallRoom
    {
        get => _activeCallRoom;
        private set
        {
            if (SetProperty(ref _activeCallRoom, value))
            {
                OnPropertyChanged(nameof(CallStatusText));
            }
        }
    }

    public int CallSecondsRemaining
    {
        get => _callSecondsRemaining;
        private set
        {
            if (SetProperty(ref _callSecondsRemaining, Math.Max(0, value)))
            {
                OnPropertyChanged(nameof(CallCountdownText));
                OnPropertyChanged(nameof(ExtensionCountdownNumber));
                OnPropertyChanged(nameof(ExtensionCountdownNote));
            }
        }
    }

    public bool IsExtensionPromptVisible
    {
        get => _isExtensionPromptVisible;
        private set
        {
            if (SetProperty(ref _isExtensionPromptVisible, value))
            {
                OnPropertyChanged(nameof(IsInitiatorExtensionPromptVisible));
                OnPropertyChanged(nameof(IsRecipientEndingWarningVisible));
                OnPropertyChanged(nameof(CallCountdownText));
                ContinueCallCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsExtensionActionBusy
    {
        get => _isExtensionActionBusy;
        private set
        {
            if (SetProperty(ref _isExtensionActionBusy, value))
            {
                ContinueCallCommand.NotifyCanExecuteChanged();
                EndCurrentCallCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsInitiatorExtensionPromptVisible =>
        IsCallActive && IsCurrentUserCallInitiator && IsExtensionPromptVisible;

    public bool IsRecipientEndingWarningVisible =>
        IsCallActive && !IsCurrentUserCallInitiator && IsExtensionPromptVisible;

    public string CallCountdownText => !IsCallActive
        ? string.Empty
        : IsExtensionPromptVisible
            ? $"Tự kết thúc sau {CallSecondsRemaining} giây"
            : $"Còn lại {TimeSpan.FromSeconds(CallSecondsRemaining):mm\\:ss}";

    public string ExtensionCountdownNumber => CallSecondsRemaining.ToString("00");

    public string ExtensionCountdownNote => IsCurrentUserCallInitiator
        ? $"Nếu không bấm Có, phiên sẽ tự động kết thúc sau {CallSecondsRemaining} giây."
        : $"Nếu bên gọi không bấm Có, phiên sẽ tự động kết thúc sau {CallSecondsRemaining} giây.";

    public string CallStatusText => Role switch
    {
        AccountRole.Admin when IsCallActive => $"Đang truyền âm thanh tới {ActiveCallRoom}",
        AccountRole.Admin => "Chọn tài khoản người dùng đang online để bắt đầu nói",
        AccountRole.User when IsCallActive => $"Đang nghe thông báo từ {ActiveCallRoom}",
        AccountRole.User => "Đang chờ máy điều hành liên lạc",
        _ => "Đăng nhập để sử dụng RoomTalk"
    };

    public double MicrophoneLevel
    {
        get => _microphoneLevel;
        private set => SetProperty(ref _microphoneLevel, Math.Clamp(value, 0d, 100d));
    }

    public string MicrophoneMeterText => Role == AccountRole.Admin
        ? IsCallActive ? "Microphone đang truyền" : "Microphone đang tắt"
        : "Tài khoản người dùng chỉ sử dụng loa";

    public string LoginButtonText => IsConnected ? "Đăng xuất" : "Đăng nhập";
    public bool IsSettingsVisible => IsConnected;

    public ObservableCollection<RoomItem> Rooms { get; } = new();

    public IAsyncRelayCommand LoginCommand { get; }
    public IRelayCommand OpenSettingsCommand { get; }
    public IAsyncRelayCommand ContinueCallCommand { get; }
    public IAsyncRelayCommand EndCurrentCallCommand { get; }

    public async Task ToggleCallAsync(RoomItem room)
    {
        if (!IsConnected)
        {
            return;
        }

        if (Role != AccountRole.Admin)
        {
            StatusMessage = "Tài khoản người dùng chỉ có chế độ nghe.";
            return;
        }

        await _sessionLock.WaitAsync();
        try
        {
            if (IsCallActive)
            {
                if (!ActiveCallRoom.Equals(room.Name, StringComparison.OrdinalIgnoreCase))
                {
                    StatusMessage = $"Hãy kết thúc liên lạc với {ActiveCallRoom} trước.";
                    return;
                }

                StatusMessage = $"Đang kết thúc liên lạc với {room.Name}...";
                await _clientService.StopCallAsync(room.Name);
                return;
            }

            StatusMessage = $"Đang bắt đầu truyền âm thanh tới {room.Name}...";
            (bool granted, string message) = await _clientService.StartCallAsync(room.Name);
            StatusMessage = message;
        }
        catch (Exception exception)
        {
            StatusMessage = $"Không thể thay đổi phiên liên lạc: {exception.Message}";
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private bool CanContinueCall()
    {
        return IsConnected &&
               IsCurrentUserCallInitiator &&
               IsCallActive &&
               IsExtensionPromptVisible &&
               !IsExtensionActionBusy;
    }

    private async Task ContinueCallAsync()
    {
        if (!CanContinueCall())
        {
            return;
        }

        IsExtensionActionBusy = true;
        StatusMessage = $"Đang tiếp tục liên lạc với {ActiveCallRoom}...";
        try
        {
            (bool granted, string message) = await _clientService.ExtendCallAsync(ActiveCallRoom);
            StatusMessage = message;
            if (granted)
            {
                IsExtensionPromptVisible = false;
                CallSecondsRemaining = 60;
            }
        }
        catch (Exception exception)
        {
            StatusMessage = $"Không thể gia hạn phiên: {exception.Message}";
        }
        finally
        {
            IsExtensionActionBusy = false;
        }
    }

    private bool CanEndCurrentCall()
    {
        return IsConnected && IsCallActive && !IsExtensionActionBusy;
    }

    private async Task EndCurrentCallAsync()
    {
        if (!CanEndCurrentCall() || string.IsNullOrWhiteSpace(ActiveCallRoom))
        {
            return;
        }

        IsExtensionActionBusy = true;
        StatusMessage = $"Đang kết thúc liên lạc với {ActiveCallRoom}...";
        try
        {
            await _clientService.StopCallAsync(ActiveCallRoom);
        }
        catch (Exception exception)
        {
            StatusMessage = $"Không thể kết thúc phiên: {exception.Message}";
        }
        finally
        {
            IsExtensionActionBusy = false;
        }
    }

    private async Task LoginOrLogoutAsync()
    {
        if (IsConnected)
        {
            await DisconnectAsync("Đã đăng xuất.");
            return;
        }

        LoginDialogData? dialogResult = _windowService.ShowLoginDialog(new LoginDialogData
        {
            ServerIp = _serverIp,
            ServerPort = _serverPort,
            Username = _username,
            Password = _password
        });
        if (dialogResult is null)
        {
            return;
        }

        _serverIp = dialogResult.ServerIp.Trim();
        _serverPort = dialogResult.ServerPort.Trim();
        _username = dialogResult.Username.Trim();
        _password = dialogResult.Password;
        OnPropertyChanged(nameof(CurrentUsername));

        if (_serverService.AuthenticateServerAccount(_username, _password))
        {
            _password = string.Empty;
            StatusMessage = "Đã đăng nhập tài khoản máy chủ.";
            _windowService.ShowTestServerWindow();
            return;
        }

        if (!int.TryParse(_serverPort, out int port) || port is < 1 or > 65535)
        {
            StatusMessage = "Cổng máy chủ không hợp lệ.";
            return;
        }

        _audioSettings.ServerAddress = _serverIp;
        _audioSettings.ServerPort = port;
        try { AudioSettingsStore.Save(_audioSettings); } catch { }

        ConnectionStatus = $"Đang kết nối {_serverIp}:{port}...";
        StatusMessage = "Đang đăng nhập...";
        LoginSessionResult result = await _clientService.ConnectAsync(
            _serverIp,
            port,
            _username,
            _password);

        if (!result.Success)
        {
            ConnectionStatus = "Chưa kết nối";
            AccountStatus = "Chưa đăng nhập";
            StatusMessage = result.Message;
            MessageBox.Show(
                $"{result.Message}\n\nĐịa chỉ đã nhập: {_serverIp}:{port}.\nVui lòng kiểm tra IP, cổng và trạng thái máy chủ rồi đăng nhập lại.",
                "Không kết nối được RoomTalk",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _username = result.Username;
        Role = result.Role;
        _allowedRooms = result.Rooms;
        IsConnected = true;
        ConnectionStatus = $"Đã kết nối {_serverIp}:{port}";
        AccountStatus = $"{_username} · {RoleText}";
        StatusMessage = result.Message;
        OnPropertyChanged(nameof(CurrentUsername));

        try
        {
            _audioCommunicationService.ApplySettings(_audioSettings);
            await _audioCommunicationService.PreparePlaybackAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = $"Đã đăng nhập nhưng chưa mở được loa: {exception.Message}";
        }

        RebuildRooms();
        OnPropertyChanged(nameof(CallStatusText));
    }

    private async Task DisconnectAsync(string message)
    {
        if (IsCallActive && !string.IsNullOrWhiteSpace(ActiveCallRoom))
        {
            try { await _clientService.StopCallAsync(ActiveCallRoom); } catch { }
        }

        await StopCallAudioAsync();
        _audioCommunicationService.StopPlayback();
        await _clientService.DisconnectAsync();

        _activeCallId = Guid.Empty;
        IsCurrentUserCallInitiator = false;
        CallSecondsRemaining = 0;
        IsExtensionPromptVisible = false;
        IsConnected = false;
        Role = AccountRole.User;
        _allowedRooms = [];
        _roomStates.Clear();
        Rooms.Clear();
        ConnectionStatus = "Chưa kết nối";
        AccountStatus = "Chưa đăng nhập";
        StatusMessage = message;
    }

    private void OpenSettings()
    {
        AudioSettingsData? result = _windowService.ShowAudioSettingsDialog(_audioSettings);
        if (result is null)
        {
            return;
        }

        _audioSettings = result;
        try { AudioSettingsStore.Save(_audioSettings); } catch { }
        SelectedMicrophone = result.InputDeviceName ?? "Không phát hiện microphone";
        SelectedSpeaker = result.OutputDeviceName ?? "Không phát hiện loa";
        _audioCommunicationService.StopPlayback();
        _audioCommunicationService.ApplySettings(_audioSettings);
        if (IsConnected && !IsCallActive)
        {
            _ = PreparePlaybackAfterSettingsChangedAsync();
        }
        StatusMessage = "Đã lưu cài đặt âm thanh.";
    }

    private async Task PreparePlaybackAfterSettingsChangedAsync()
    {
        try { await _audioCommunicationService.PreparePlaybackAsync(); }
        catch (Exception exception) { StatusMessage = $"Không mở được loa: {exception.Message}"; }
    }

    private void ClientServiceOnRoomStateChanged(RoomRuntimeState state)
    {
        RunOnUiThread(() =>
        {
            if (Role != AccountRole.Admin)
            {
                return;
            }

            _roomStates[state.RoomName] = state;
            if (!state.RoomName.Equals(_username, StringComparison.OrdinalIgnoreCase) &&
                !_allowedRooms.Contains(state.RoomName, StringComparer.OrdinalIgnoreCase))
            {
                _allowedRooms = [.. _allowedRooms, state.RoomName];
            }
            RebuildRooms();
        });
    }

    private void ClientServiceOnCallSessionChanged(CallSessionState state)
    {
        RunOnUiThread(() => _ = ApplyCallSessionAsync(state));
    }

    private void ClientServiceOnCallTimerChanged(CallTimerState state)
    {
        RunOnUiThread(() => ApplyCallTimerState(state));
    }

    private void ApplyCallTimerState(CallTimerState state)
    {
        if (!IsCallActive ||
            !ActiveCallRoom.Equals(state.RoomName, StringComparison.OrdinalIgnoreCase) ||
            (_activeCallId != Guid.Empty && state.CallId != Guid.Empty && _activeCallId != state.CallId))
        {
            return;
        }

        if (_activeCallId == Guid.Empty)
        {
            _activeCallId = state.CallId;
        }

        IsCurrentUserCallInitiator = state.IsCurrentUserInitiator;
        CallSecondsRemaining = state.SecondsRemaining;
        IsExtensionPromptVisible = state.IsExtensionConfirmationRequired;

        if (!string.IsNullOrWhiteSpace(state.Message))
        {
            StatusMessage = state.Message;
        }
    }

    private async Task ApplyCallSessionAsync(CallSessionState state)
    {
        if (state.IsActive)
        {
            bool wasAlreadyActive = IsCallActive &&
                                    ActiveCallRoom.Equals(state.RoomName, StringComparison.OrdinalIgnoreCase);
            _activeCallId = state.CallId;
            ActiveCallRoom = state.RoomName;
            IsCurrentUserCallInitiator = state.IsCurrentUserInitiator;
            IsCallActive = true;
            CallSecondsRemaining = 60;
            IsExtensionPromptVisible = false;
            StatusMessage = state.Message;
            RebuildRooms();

            if (wasAlreadyActive || _isStartingAudio)
            {
                return;
            }

            _isStartingAudio = true;
            try
            {
                _audioCommunicationService.ApplySettings(_audioSettings);
                if (Role == AccountRole.Admin)
                {
                    // User không gửi audio ngược lại, vì vậy mở mic ngay, không cần chờ đuôi loa cục bộ.
                    await _audioCommunicationService.StartDuplexAsync();
                }
                else
                {
                    // Tài khoản User không mở microphone; loa tự phát ngay khi Admin bắt đầu phiên.
                    await _audioCommunicationService.PreparePlaybackAsync();
                }
            }
            catch (Exception exception)
            {
                StatusMessage = $"Phiên đã mở nhưng không khởi động được âm thanh: {exception.Message}";
            }
            finally
            {
                _isStartingAudio = false;
            }
        }
        else
        {
            await StopCallAudioAsync();
            _activeCallId = Guid.Empty;
            IsCurrentUserCallInitiator = false;
            CallSecondsRemaining = 0;
            IsExtensionPromptVisible = false;
            ActiveCallRoom = string.Empty;
            IsCallActive = false;
            StatusMessage = state.Message;
            RebuildRooms();
        }
    }

    private async Task StopCallAudioAsync()
    {
        try
        {
            if (_audioCommunicationService.IsTalking)
            {
                await _audioCommunicationService.StopTalkingAsync();
            }
        }
        catch { }

        MicrophoneLevel = 0;
        _activeCallId = Guid.Empty;
        IsCurrentUserCallInitiator = false;
        CallSecondsRemaining = 0;
        IsExtensionPromptVisible = false;
        IsCallActive = false;
        ActiveCallRoom = string.Empty;
    }

    private void RebuildRooms()
    {
        Rooms.Clear();
        if (!IsConnected || Role != AccountRole.Admin)
        {
            OnPropertyChanged(nameof(CallStatusText));
            return;
        }

        foreach (string accountName in _allowedRooms
                     .Where(name => !name.Equals(_username, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            _roomStates.TryGetValue(accountName, out RoomRuntimeState? state);
            bool online = state?.OnlineClients > 0;
            bool activeHere = IsCallActive && ActiveCallRoom.Equals(accountName, StringComparison.OrdinalIgnoreCase);
            bool busyByOther = state?.IsSessionActive == true && !activeHere;
            string action = activeHere
                ? "Dừng nói"
                : !online
                    ? "Đang offline"
                    : busyByOther
                        ? "Đang bận"
                        : "Bắt đầu nói";

            Rooms.Add(new RoomItem(
                accountName,
                activeHere || state?.IsSessionActive == true
                    ? RoomState.Broadcasting
                    : online ? RoomState.Online : RoomState.Offline,
                online ? 1 : 0,
                1,
                canTalk: activeHere || (online && !busyByOther && !IsCallActive),
                actionText: action));
        }

        OnPropertyChanged(nameof(CallStatusText));
    }

    private void ClientServiceOnConnectionLost(string reason)
    {
        RunOnUiThread(() => _ = DisconnectAsync(reason));
    }

    private void SelectDefaultAudioDevices(IAudioDeviceService audioDeviceService)
    {
        IReadOnlyList<AudioDeviceItem> inputs = audioDeviceService.GetInputDevices();
        IReadOnlyList<AudioDeviceItem> outputs = audioDeviceService.GetOutputDevices();
        AudioDeviceItem? input = inputs.FirstOrDefault(item => item.Id == _audioSettings.InputDeviceId)
                                 ?? inputs.FirstOrDefault(item => item.IsDefault)
                                 ?? inputs.FirstOrDefault();
        AudioDeviceItem? output = outputs.FirstOrDefault(item => item.Id == _audioSettings.OutputDeviceId)
                                  ?? outputs.FirstOrDefault(item => item.IsDefault)
                                  ?? outputs.FirstOrDefault();
        _audioSettings.InputDeviceId = input?.Id;
        _audioSettings.InputDeviceName = input?.Name;
        _audioSettings.OutputDeviceId = output?.Id;
        _audioSettings.OutputDeviceName = output?.Name;
        SelectedMicrophone = input?.Name ?? "Không phát hiện microphone";
        SelectedSpeaker = output?.Name ?? "Không phát hiện loa";
    }

    private void AudioCommunicationServiceOnMicrophoneLevelChanged(double level)
    {
        RunOnUiThread(() => MicrophoneLevel = IsCallActive && Role == AccountRole.Admin ? level : 0);
    }

    private static void RunOnUiThread(Action action)
    {
        if (Application.Current?.Dispatcher is not { } dispatcher || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _clientService.RoomStateChanged -= ClientServiceOnRoomStateChanged;
        _clientService.CallSessionChanged -= ClientServiceOnCallSessionChanged;
        _clientService.CallTimerChanged -= ClientServiceOnCallTimerChanged;
        _clientService.ConnectionLost -= ClientServiceOnConnectionLost;
        _audioCommunicationService.MicrophoneLevelChanged -= AudioCommunicationServiceOnMicrophoneLevelChanged;
        await DisconnectAsync("Đã đóng RoomTalk.");
        await _audioCommunicationService.DisposeAsync();
        await _clientService.DisposeAsync();
        _sessionLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
