using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoomTalk.Models;
using RoomTalk.Services;

namespace RoomTalk.ViewModels;

public sealed class AdminViewModel : ObservableObject, IDisposable
{
    private readonly INetworkSpeakerService _networkSpeakerService;
    private CancellationTokenSource? _scanCancellation;

    private string _scanStartIp = "192.168.1.1";
    private string _scanEndIp = "192.168.1.254";
    private string _newSpeakerName = string.Empty;
    private string _newSpeakerIp = string.Empty;
    private string _newSpeakerPort = "80";
    private string _selectedRoom = "Phòng 101";
    private NetworkSpeakerItem? _selectedNetworkSpeaker;
    private bool _isScanning;
    private int _scanPercent;
    private string _scanProgressText = "Chưa quét dải mạng.";
    private string _networkStatusMessage =
        "Quét dải IP hoặc thêm loa bằng IP. Bước này mới quản lý và nhận diện thiết bị, chưa truyền âm thanh tới loa.";

    public AdminViewModel(INetworkSpeakerService networkSpeakerService)
    {
        _networkSpeakerService = networkSpeakerService;

        RoomNames = new ObservableCollection<string>
        {
            "Phòng 101",
            "Phòng 102",
            "Phòng 103",
            "Phòng họp",
            "Hành lang",
            "Chưa gán phòng"
        };

        ScanNetworkCommand = new AsyncRelayCommand(ScanNetworkAsync, CanScanNetwork);
        CancelScanCommand = new RelayCommand(CancelScan, () => IsScanning);
        AddManualSpeakerCommand = new AsyncRelayCommand(AddManualSpeakerAsync, CanAddManualSpeaker);
        RefreshSelectedSpeakerCommand = new AsyncRelayCommand(
            RefreshSelectedSpeakerAsync,
            () => SelectedNetworkSpeaker is not null && !IsScanning);
        RefreshAllSpeakersCommand = new AsyncRelayCommand(RefreshAllSpeakersAsync, CanRefreshAll);
        AssignRoomCommand = new AsyncRelayCommand(
            AssignRoomAsync,
            () => SelectedNetworkSpeaker is not null && !string.IsNullOrWhiteSpace(SelectedRoom));
        DeleteSelectedSpeakerCommand = new AsyncRelayCommand(
            DeleteSelectedSpeakerAsync,
            () => SelectedNetworkSpeaker is not null);
        SaveSpeakersCommand = new AsyncRelayCommand(SaveSpeakersAsync);

        _ = LoadSpeakersAsync();
    }

    public ObservableCollection<NetworkSpeakerItem> NetworkSpeakers { get; } = new();

    public ObservableCollection<string> RoomNames { get; }

    public string ScanStartIp
    {
        get => _scanStartIp;
        set
        {
            if (SetProperty(ref _scanStartIp, value))
            {
                ScanNetworkCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string ScanEndIp
    {
        get => _scanEndIp;
        set
        {
            if (SetProperty(ref _scanEndIp, value))
            {
                ScanNetworkCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string NewSpeakerName
    {
        get => _newSpeakerName;
        set => SetProperty(ref _newSpeakerName, value);
    }

    public string NewSpeakerIp
    {
        get => _newSpeakerIp;
        set
        {
            if (SetProperty(ref _newSpeakerIp, value))
            {
                AddManualSpeakerCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string NewSpeakerPort
    {
        get => _newSpeakerPort;
        set
        {
            if (SetProperty(ref _newSpeakerPort, value))
            {
                AddManualSpeakerCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string SelectedRoom
    {
        get => _selectedRoom;
        set
        {
            if (SetProperty(ref _selectedRoom, value))
            {
                AssignRoomCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public NetworkSpeakerItem? SelectedNetworkSpeaker
    {
        get => _selectedNetworkSpeaker;
        set
        {
            if (SetProperty(ref _selectedNetworkSpeaker, value))
            {
                RefreshSelectedSpeakerCommand.NotifyCanExecuteChanged();
                AssignRoomCommand.NotifyCanExecuteChanged();
                DeleteSelectedSpeakerCommand.NotifyCanExecuteChanged();

                if (value is not null && RoomNames.Contains(value.RoomName))
                {
                    SelectedRoom = value.RoomName;
                }
            }
        }
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
            {
                ScanNetworkCommand.NotifyCanExecuteChanged();
                CancelScanCommand.NotifyCanExecuteChanged();
                RefreshSelectedSpeakerCommand.NotifyCanExecuteChanged();
                RefreshAllSpeakersCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public int ScanPercent
    {
        get => _scanPercent;
        private set => SetProperty(ref _scanPercent, value);
    }

    public string ScanProgressText
    {
        get => _scanProgressText;
        private set => SetProperty(ref _scanProgressText, value);
    }

    public string NetworkStatusMessage
    {
        get => _networkStatusMessage;
        private set => SetProperty(ref _networkStatusMessage, value);
    }

    public IAsyncRelayCommand ScanNetworkCommand { get; }
    public IRelayCommand CancelScanCommand { get; }
    public IAsyncRelayCommand AddManualSpeakerCommand { get; }
    public IAsyncRelayCommand RefreshSelectedSpeakerCommand { get; }
    public IAsyncRelayCommand RefreshAllSpeakersCommand { get; }
    public IAsyncRelayCommand AssignRoomCommand { get; }
    public IAsyncRelayCommand DeleteSelectedSpeakerCommand { get; }
    public IAsyncRelayCommand SaveSpeakersCommand { get; }

    private async Task LoadSpeakersAsync()
    {
        try
        {
            IReadOnlyList<NetworkSpeakerItem> saved =
                await _networkSpeakerService.LoadAsync();

            NetworkSpeakers.Clear();
            foreach (NetworkSpeakerItem speaker in saved)
            {
                NetworkSpeakers.Add(speaker);
            }

            NetworkStatusMessage = saved.Count == 0
                ? "Chưa có loa IP. Hãy quét dải mạng hoặc thêm IP thủ công."
                : $"Đã nạp {saved.Count} loa IP từ cấu hình JSON.";

            RefreshAllSpeakersCommand.NotifyCanExecuteChanged();
        }
        catch (Exception exception)
        {
            NetworkStatusMessage = $"Không đọc được cấu hình loa IP: {exception.Message}";
        }
    }

    private bool CanScanNetwork()
    {
        return !IsScanning &&
               IPAddress.TryParse(ScanStartIp, out _) &&
               IPAddress.TryParse(ScanEndIp, out _);
    }

    private async Task ScanNetworkAsync()
    {
        _scanCancellation?.Dispose();
        _scanCancellation = new CancellationTokenSource();
        IsScanning = true;
        ScanPercent = 0;
        ScanProgressText = "Đang chuẩn bị quét dải mạng...";
        NetworkStatusMessage =
            "Đang tìm các thiết bị có HTTP/HTTPS/RTSP hoặc cổng SDK Hikvision 8000.";

        var progress = new Progress<NetworkScanProgress>(value =>
        {
            ScanPercent = value.Percent;
            ScanProgressText =
                $"Đã kiểm tra {value.Completed}/{value.Total} · IP {value.CurrentIp} · Tìm thấy {value.Found}";
        });

        try
        {
            IReadOnlyList<NetworkSpeakerItem> found =
                await _networkSpeakerService.ScanAsync(
                    ScanStartIp.Trim(),
                    ScanEndIp.Trim(),
                    progress,
                    _scanCancellation.Token);

            int added = 0;
            int updated = 0;

            foreach (NetworkSpeakerItem speaker in found)
            {
                NetworkSpeakerItem? existing = NetworkSpeakers.FirstOrDefault(
                    item => item.IpAddress.Equals(
                        speaker.IpAddress,
                        StringComparison.OrdinalIgnoreCase));

                if (existing is null)
                {
                    NetworkSpeakers.Add(speaker);
                    added++;
                }
                else
                {
                    CopyProbeResult(speaker, existing);
                    updated++;
                }
            }

            await _networkSpeakerService.SaveAsync(NetworkSpeakers);

            ScanPercent = 100;
            ScanProgressText =
                $"Hoàn tất · thêm {added}, cập nhật {updated}, tổng {NetworkSpeakers.Count} thiết bị.";
            NetworkStatusMessage = found.Count == 0
                ? "Không tìm thấy thiết bị trên các cổng đang kiểm tra. Có thể thêm loa bằng IP thủ công."
                : "Đã lưu kết quả quét. Thiết bị màu vàng đang yêu cầu tài khoản để đọc thông tin ISAPI.";
        }
        catch (OperationCanceledException)
        {
            ScanProgressText = "Đã hủy quét dải mạng.";
            NetworkStatusMessage = "Quá trình quét đã được hủy.";
        }
        catch (Exception exception)
        {
            ScanProgressText = "Quét dải mạng thất bại.";
            NetworkStatusMessage = exception.Message;
        }
        finally
        {
            IsScanning = false;
            _scanCancellation?.Dispose();
            _scanCancellation = null;
            RefreshAllSpeakersCommand.NotifyCanExecuteChanged();
        }
    }

    private void CancelScan()
    {
        _scanCancellation?.Cancel();
    }

    private bool CanAddManualSpeaker()
    {
        return !IsScanning &&
               IPAddress.TryParse(NewSpeakerIp, out _) &&
               int.TryParse(NewSpeakerPort, out int port) &&
               port is > 0 and <= 65535;
    }

    private async Task AddManualSpeakerAsync()
    {
        string ipAddress = NewSpeakerIp.Trim();

        if (NetworkSpeakers.Any(item =>
                item.IpAddress.Equals(ipAddress, StringComparison.OrdinalIgnoreCase)))
        {
            NetworkStatusMessage = $"IP {ipAddress} đã có trong danh sách.";
            return;
        }

        try
        {
            NetworkStatusMessage = $"Đang kiểm tra thiết bị {ipAddress}...";
            NetworkSpeakerItem speaker = await _networkSpeakerService.ProbeAsync(ipAddress);

            if (!string.IsNullOrWhiteSpace(NewSpeakerName))
            {
                speaker.Name = NewSpeakerName.Trim();
            }

            if (int.TryParse(NewSpeakerPort, out int port))
            {
                speaker.ManagementPort = port;
            }

            speaker.RoomName = SelectedRoom;
            NetworkSpeakers.Add(speaker);
            SelectedNetworkSpeaker = speaker;

            await _networkSpeakerService.SaveAsync(NetworkSpeakers);

            NewSpeakerName = string.Empty;
            NewSpeakerIp = string.Empty;
            NetworkStatusMessage = speaker.IsReachable
                ? $"Đã thêm {speaker.Name} ({speaker.IpAddress})."
                : $"Đã lưu {speaker.IpAddress}, nhưng hiện chưa kết nối được thiết bị.";

            RefreshAllSpeakersCommand.NotifyCanExecuteChanged();
        }
        catch (Exception exception)
        {
            NetworkStatusMessage = $"Không thêm được loa IP: {exception.Message}";
        }
    }

    private async Task RefreshSelectedSpeakerAsync()
    {
        if (SelectedNetworkSpeaker is null)
        {
            return;
        }

        NetworkSpeakerItem target = SelectedNetworkSpeaker;
        target.Status = NetworkSpeakerStatus.Scanning;
        NetworkStatusMessage = $"Đang kiểm tra {target.IpAddress}...";

        try
        {
            NetworkSpeakerItem probed =
                await _networkSpeakerService.ProbeAsync(target.IpAddress);
            CopyProbeResult(probed, target);
            await _networkSpeakerService.SaveAsync(NetworkSpeakers);
            NetworkStatusMessage =
                $"{target.Name}: {target.StatusText} · {target.Protocol}";
        }
        catch (Exception exception)
        {
            target.Status = NetworkSpeakerStatus.Error;
            target.Note = exception.Message;
            NetworkStatusMessage = $"Kiểm tra thiết bị lỗi: {exception.Message}";
        }
    }

    private bool CanRefreshAll()
    {
        return !IsScanning && NetworkSpeakers.Count > 0;
    }

    private async Task RefreshAllSpeakersAsync()
    {
        if (NetworkSpeakers.Count == 0)
        {
            return;
        }

        IsScanning = true;
        int completed = 0;
        int online = 0;

        try
        {
            foreach (NetworkSpeakerItem target in NetworkSpeakers.ToList())
            {
                target.Status = NetworkSpeakerStatus.Scanning;

                try
                {
                    NetworkSpeakerItem probed =
                        await _networkSpeakerService.ProbeAsync(target.IpAddress);
                    CopyProbeResult(probed, target);
                    if (target.IsReachable)
                    {
                        online++;
                    }
                }
                catch (Exception exception)
                {
                    target.Status = NetworkSpeakerStatus.Error;
                    target.Note = exception.Message;
                }

                completed++;
                ScanPercent = (int)Math.Round(completed * 100d / NetworkSpeakers.Count);
                ScanProgressText =
                    $"Đang làm mới {completed}/{NetworkSpeakers.Count} · Online {online}";
            }

            await _networkSpeakerService.SaveAsync(NetworkSpeakers);
            NetworkStatusMessage =
                $"Đã làm mới trạng thái: {online}/{NetworkSpeakers.Count} thiết bị có phản hồi.";
        }
        finally
        {
            IsScanning = false;
        }
    }

    private async Task AssignRoomAsync()
    {
        if (SelectedNetworkSpeaker is null)
        {
            return;
        }

        SelectedNetworkSpeaker.RoomName = SelectedRoom;
        await _networkSpeakerService.SaveAsync(NetworkSpeakers);
        NetworkStatusMessage =
            $"Đã gán {SelectedNetworkSpeaker.Name} vào {SelectedRoom}.";
    }

    private async Task DeleteSelectedSpeakerAsync()
    {
        if (SelectedNetworkSpeaker is null)
        {
            return;
        }

        NetworkSpeakerItem target = SelectedNetworkSpeaker;
        NetworkSpeakers.Remove(target);
        SelectedNetworkSpeaker = null;
        await _networkSpeakerService.SaveAsync(NetworkSpeakers);
        NetworkStatusMessage = $"Đã xóa {target.Name} khỏi cấu hình.";
        RefreshAllSpeakersCommand.NotifyCanExecuteChanged();
    }

    private async Task SaveSpeakersAsync()
    {
        try
        {
            await _networkSpeakerService.SaveAsync(NetworkSpeakers);
            NetworkStatusMessage =
                $"Đã lưu {NetworkSpeakers.Count} thiết bị vào network-speakers.json.";
        }
        catch (Exception exception)
        {
            NetworkStatusMessage = $"Không lưu được cấu hình: {exception.Message}";
        }
    }

    private static void CopyProbeResult(
        NetworkSpeakerItem source,
        NetworkSpeakerItem target)
    {
        target.ManagementPort = source.ManagementPort;
        target.Manufacturer = source.Manufacturer;
        target.Model = source.Model;
        target.Protocol = source.Protocol;
        target.Status = source.Status;
        target.LastSeen = source.LastSeen;
        target.Note = source.Note;
    }

    public void Dispose()
    {
        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        _scanCancellation = null;
    }
}
