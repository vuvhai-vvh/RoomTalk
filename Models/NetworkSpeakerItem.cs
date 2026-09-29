using CommunityToolkit.Mvvm.ComponentModel;

namespace RoomTalk.Models;

public sealed class NetworkSpeakerItem : ObservableObject
{
    private Guid _id = Guid.NewGuid();
    private string _name = "Loa IP";
    private string _ipAddress = string.Empty;
    private int _managementPort = 80;
    private string _manufacturer = "Chưa xác định";
    private string _model = "Chưa xác định";
    private string _protocol = string.Empty;
    private string _roomName = "Chưa gán phòng";
    private NetworkSpeakerStatus _status = NetworkSpeakerStatus.Unknown;
    private DateTimeOffset? _lastSeen;
    private string _note = string.Empty;

    public Guid Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string IpAddress
    {
        get => _ipAddress;
        set => SetProperty(ref _ipAddress, value);
    }

    public int ManagementPort
    {
        get => _managementPort;
        set => SetProperty(ref _managementPort, value);
    }

    public string Manufacturer
    {
        get => _manufacturer;
        set
        {
            if (SetProperty(ref _manufacturer, value))
            {
                OnPropertyChanged(nameof(IsHikvisionCandidate));
            }
        }
    }

    public string Model
    {
        get => _model;
        set => SetProperty(ref _model, value);
    }

    public string Protocol
    {
        get => _protocol;
        set => SetProperty(ref _protocol, value);
    }

    public string RoomName
    {
        get => _roomName;
        set => SetProperty(ref _roomName, value);
    }

    public NetworkSpeakerStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsOnline));
                OnPropertyChanged(nameof(IsScanning));
                OnPropertyChanged(nameof(HasWarning));
                OnPropertyChanged(nameof(IsReachable));
            }
        }
    }

    public DateTimeOffset? LastSeen
    {
        get => _lastSeen;
        set
        {
            if (SetProperty(ref _lastSeen, value))
            {
                OnPropertyChanged(nameof(LastSeenText));
            }
        }
    }

    public string Note
    {
        get => _note;
        set => SetProperty(ref _note, value);
    }

    public string StatusText => Status switch
    {
        NetworkSpeakerStatus.Scanning => "Đang kiểm tra",
        NetworkSpeakerStatus.Online => "Online",
        NetworkSpeakerStatus.AuthenticationRequired => "Cần đăng nhập",
        NetworkSpeakerStatus.Offline => "Offline",
        NetworkSpeakerStatus.Error => "Có lỗi",
        _ => "Chưa kiểm tra"
    };

    public string LastSeenText => LastSeen?.LocalDateTime.ToString("dd/MM/yyyy HH:mm:ss") ?? "—";

    public bool IsOnline => Status == NetworkSpeakerStatus.Online;

    public bool IsScanning => Status == NetworkSpeakerStatus.Scanning;

    public bool HasWarning => Status is NetworkSpeakerStatus.AuthenticationRequired or NetworkSpeakerStatus.Error;

    public bool IsReachable => Status is NetworkSpeakerStatus.Online or NetworkSpeakerStatus.AuthenticationRequired;

    public bool IsHikvisionCandidate =>
        Manufacturer.Contains("Hikvision", StringComparison.OrdinalIgnoreCase) ||
        Protocol.Contains("ISAPI", StringComparison.OrdinalIgnoreCase) ||
        Protocol.Contains("8000", StringComparison.OrdinalIgnoreCase);
}
