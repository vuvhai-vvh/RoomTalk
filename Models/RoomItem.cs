using CommunityToolkit.Mvvm.ComponentModel;

namespace RoomTalk.Models;

public sealed class RoomItem : ObservableObject
{
    private string _name;
    private RoomState _state;
    private int _onlineClients;
    private int _totalClients;
    private bool _canTalk;
    private string _actionText;

    public RoomItem(
        string name,
        RoomState state,
        int onlineClients,
        int totalClients,
        bool canTalk,
        string actionText = "Bấm để nói")
    {
        _name = name;
        _state = state;
        _onlineClients = onlineClients;
        _totalClients = totalClients;
        _canTalk = canTalk;
        _actionText = actionText;
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public RoomState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsOnline));
                OnPropertyChanged(nameof(IsBroadcasting));
            }
        }
    }

    public bool IsOnline => State == RoomState.Online;

    public bool IsBroadcasting => State == RoomState.Broadcasting;

    public int OnlineClients
    {
        get => _onlineClients;
        set
        {
            if (SetProperty(ref _onlineClients, value))
            {
                OnPropertyChanged(nameof(OnlineSummary));
            }
        }
    }

    public int TotalClients
    {
        get => _totalClients;
        set
        {
            if (SetProperty(ref _totalClients, value))
            {
                OnPropertyChanged(nameof(OnlineSummary));
            }
        }
    }

    public bool CanTalk
    {
        get => _canTalk;
        set => SetProperty(ref _canTalk, value);
    }

    public string ActionText
    {
        get => _actionText;
        set => SetProperty(ref _actionText, value);
    }

    public string OnlineSummary => State switch
    {
        RoomState.Broadcasting => "Đang liên lạc",
        RoomState.Online => "Đang trực tuyến",
        _ => "Ngoại tuyến"
    };
}
