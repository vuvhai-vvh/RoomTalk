using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoomTalk.Models;
using RoomTalk.Services;

namespace RoomTalk.ViewModels;

public sealed class LoginViewModel : ObservableObject
{
    private readonly IRoomTalkServerService _serverService;
    private string _serverIp;
    private string _serverPort;
    private string _username;
    private string _password;
    private string _statusMessage;

    public LoginViewModel(
        LoginDialogData currentData,
        IRoomTalkServerService serverService)
    {
        _serverService = serverService;
        _serverIp = currentData.ServerIp;
        _serverPort = currentData.ServerPort;
        _username = currentData.Username;
        _password = currentData.Password;
        _statusMessage = IsLocalServerAccount
            ? "Tài khoản máy chủ đăng nhập trực tiếp trên máy này, không cần IP và cổng."
            : "Nhập IP máy chủ, cổng và tài khoản RoomTalk.";

        ConnectCommand = new RelayCommand(Connect);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
    }

    public event Action<bool>? RequestClose;
    public LoginDialogData? Result { get; private set; }

    public string ServerIp
    {
        get => _serverIp;
        set => SetProperty(ref _serverIp, value);
    }

    public string ServerPort
    {
        get => _serverPort;
        set => SetProperty(ref _serverPort, value);
    }

    public string Username
    {
        get => _username;
        set
        {
            if (!SetProperty(ref _username, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsLocalServerAccount));
            OnPropertyChanged(nameof(IsNetworkLogin));
            OnPropertyChanged(nameof(ConnectionModeMessage));
            StatusMessage = IsLocalServerAccount
                ? "Tài khoản máy chủ đăng nhập trực tiếp trên máy này, không cần IP và cổng."
                : "Nhập IP máy chủ, cổng và tài khoản RoomTalk.";
        }
    }

    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    public bool IsLocalServerAccount => _serverService.IsServerAccountUsername(Username);
    public bool IsNetworkLogin => !IsLocalServerAccount;

    public string ConnectionModeMessage => IsLocalServerAccount
        ? "Đăng nhập máy chủ cục bộ — không sử dụng kết nối IP."
        : "Nhập địa chỉ máy chủ để kết nối tài khoản Admin hoặc User.";

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public IRelayCommand ConnectCommand { get; }
    public IRelayCommand CancelCommand { get; }

    private void Connect()
    {
        string username = Username.Trim();

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(Password))
        {
            StatusMessage = "Vui lòng nhập đầy đủ tài khoản và mật khẩu.";
            return;
        }

        if (IsLocalServerAccount)
        {
            if (!_serverService.AuthenticateServerAccount(username, Password))
            {
                StatusMessage = "Mật khẩu tài khoản máy chủ không đúng hoặc tài khoản đã bị khóa.";
                return;
            }

            Result = new LoginDialogData
            {
                ServerIp = ServerIp.Trim(),
                ServerPort = ServerPort.Trim(),
                Username = username,
                Password = Password
            };

            RequestClose?.Invoke(true);
            return;
        }

        if (string.IsNullOrWhiteSpace(ServerIp))
        {
            StatusMessage = "Vui lòng nhập IP hoặc tên máy chủ.";
            return;
        }

        if (!int.TryParse(ServerPort, out int port) || port is < 1 or > 65535)
        {
            StatusMessage = "Cổng máy chủ phải là số trong khoảng 1–65535.";
            return;
        }

        Result = new LoginDialogData
        {
            ServerIp = ServerIp.Trim(),
            ServerPort = port.ToString(),
            Username = username,
            Password = Password
        };

        RequestClose?.Invoke(true);
    }
}
