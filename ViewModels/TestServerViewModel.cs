using System.Collections.ObjectModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoomTalk.Models;
using RoomTalk.Services;

namespace RoomTalk.ViewModels;

public sealed class TestServerViewModel : ObservableObject, IDisposable
{
    private readonly IRoomTalkServerService _serverService;
    private string _port;
    private string _statusMessage;
    private ServerAccountItem? _selectedAccount;
    private string _editUsername = string.Empty;
    private string _editPassword = string.Empty;
    private AccountRole _editRole = AccountRole.User;
    private bool _editEnabled = true;
    private bool _disposed;

    public TestServerViewModel(IRoomTalkServerService serverService)
    {
        _serverService = serverService;
        _port = serverService.Port.ToString();
        _statusMessage = "Quản trị máy chủ đã sẵn sàng.";

        StartServerCommand = new AsyncRelayCommand(StartServerAsync, () => !IsRunning);
        StopServerCommand = new AsyncRelayCommand(StopServerAsync, () => IsRunning);
        RefreshCommand = new RelayCommand(Refresh);
        NewAccountCommand = new RelayCommand(NewAccount);
        SaveAccountCommand = new RelayCommand(SaveAccount);
        DeleteAccountCommand = new RelayCommand(DeleteAccount, () => SelectedAccount is not null);
        _serverService.StatusChanged += ServerServiceOnStatusChanged;

        foreach (AccountRole role in Enum.GetValues<AccountRole>())
        {
            Roles.Add(role);
        }

        Refresh();
        NewAccount();
    }

    public ObservableCollection<ConnectedClientInfo> ConnectedClients { get; } = new();
    public ObservableCollection<ServerAccountItem> Accounts { get; } = new();
    public ObservableCollection<AccountRole> Roles { get; } = new();

    public string Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsRunning => _serverService.IsRunning;
    public string LocalIpAddresses => GetLocalIpv4Addresses();
    public bool IsEditingExistingAccount => SelectedAccount is not null;

    public ServerAccountItem? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (!SetProperty(ref _selectedAccount, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsEditingExistingAccount));
            DeleteAccountCommand.NotifyCanExecuteChanged();
            if (value is null)
            {
                return;
            }

            EditUsername = value.Username;
            EditPassword = string.Empty;
            EditRole = value.Role;
            EditEnabled = value.Enabled;
        }
    }

    public string EditUsername
    {
        get => _editUsername;
        set => SetProperty(ref _editUsername, value);
    }

    public string EditPassword
    {
        get => _editPassword;
        set => SetProperty(ref _editPassword, value);
    }

    public AccountRole EditRole
    {
        get => _editRole;
        set => SetProperty(ref _editRole, value);
    }

    public bool EditEnabled
    {
        get => _editEnabled;
        set => SetProperty(ref _editEnabled, value);
    }

    public IAsyncRelayCommand StartServerCommand { get; }
    public IAsyncRelayCommand StopServerCommand { get; }
    public IRelayCommand RefreshCommand { get; }
    public IRelayCommand NewAccountCommand { get; }
    public IRelayCommand SaveAccountCommand { get; }
    public IRelayCommand DeleteAccountCommand { get; }

    private async Task StartServerAsync()
    {
        if (!int.TryParse(Port, out int port) || port is < 1 or > 65535)
        {
            StatusMessage = "Cổng phải nằm trong khoảng 1–65535.";
            return;
        }

        try
        {
            await _serverService.StartAsync(port);
            Refresh();
        }
        catch (SocketException exception)
        {
            StatusMessage = $"Không mở được cổng {port}: {exception.Message}";
        }
        catch (Exception exception)
        {
            StatusMessage = $"Không khởi động được máy chủ: {exception.Message}";
        }
    }

    private async Task StopServerAsync()
    {
        await _serverService.StopAsync();
        Refresh();
    }

    private void NewAccount()
    {
        SelectedAccount = null;
        EditUsername = string.Empty;
        EditPassword = string.Empty;
        EditRole = AccountRole.User;
        EditEnabled = true;
    }

    private void SaveAccount()
    {
        try
        {
            _serverService.SaveAccount(EditUsername, EditPassword, EditRole, EditEnabled);
            EditPassword = string.Empty;
            StatusMessage = EditRole == AccountRole.Server
                ? "Đã lưu tài khoản máy chủ."
                : "Đã lưu tài khoản. Tên tài khoản đồng thời là tên phòng liên lạc.";
            Refresh();
            SelectedAccount = Accounts.FirstOrDefault(account =>
                account.Username.Equals(EditUsername, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
    }

    private void DeleteAccount()
    {
        if (SelectedAccount is null)
        {
            return;
        }

        try
        {
            _serverService.DeleteAccount(SelectedAccount.Username);
            StatusMessage = "Đã xóa tài khoản và phòng liên lạc tương ứng.";
            Refresh();
            NewAccount();
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
    }

    private void ServerServiceOnStatusChanged(object? sender, EventArgs e)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(Refresh));
            return;
        }

        Refresh();
    }

    private void Refresh()
    {
        StatusMessage = _serverService.StatusText;
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(LocalIpAddresses));
        StartServerCommand.NotifyCanExecuteChanged();
        StopServerCommand.NotifyCanExecuteChanged();

        ConnectedClients.Clear();
        foreach (ConnectedClientInfo client in _serverService.ConnectedClients)
        {
            ConnectedClients.Add(client);
        }

        string? selectedUsername = SelectedAccount?.Username;
        Accounts.Clear();
        foreach (ServerAccountItem account in _serverService.Accounts)
        {
            Accounts.Add(account);
        }

        if (!string.IsNullOrWhiteSpace(selectedUsername))
        {
            SelectedAccount = Accounts.FirstOrDefault(account =>
                account.Username.Equals(selectedUsername, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static string GetLocalIpv4Addresses()
    {
        try
        {
            string[] addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(network => network.OperationalStatus == OperationalStatus.Up &&
                                  network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(network => network.GetIPProperties().UnicastAddresses)
                .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork &&
                                  !IPAddress.IsLoopback(address.Address))
                .Select(address => address.Address.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return addresses.Length == 0 ? "Không tìm thấy IPv4 LAN." : string.Join("  ·  ", addresses);
        }
        catch
        {
            return "Không đọc được IPv4 LAN.";
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _serverService.StatusChanged -= ServerServiceOnStatusChanged;
        GC.SuppressFinalize(this);
    }
}
