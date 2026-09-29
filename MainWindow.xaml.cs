using System.Windows;
using System.Windows.Controls;
using RoomTalk.Models;
using RoomTalk.Services;
using RoomTalk.ViewModels;

namespace RoomTalk;

public partial class MainWindow : Window
{
    private readonly RoomTalkServerService _serverService;
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        var networkSpeakerService = new NetworkSpeakerService();
        _serverService = new RoomTalkServerService();
        var clientService = new RoomTalkClientService();
        var audioCommunicationService = new AudioCommunicationService(clientService);
        var windowService = new WindowService(networkSpeakerService, _serverService);

        _viewModel = new MainViewModel(
            windowService,
            clientService,
            _serverService,
            audioCommunicationService,
            new AudioDeviceService());

        DataContext = _viewModel;
        Closed += MainWindow_OnClosed;
    }

    private async void TalkButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: RoomItem room })
        {
            await _viewModel.ToggleCallAsync(room);
        }
    }

    private async void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        await _viewModel.DisposeAsync();
        _serverService.Dispose();
    }
}
