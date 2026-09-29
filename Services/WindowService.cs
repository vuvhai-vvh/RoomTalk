using System.Windows;
using RoomTalk.Models;
using RoomTalk.ViewModels;

namespace RoomTalk.Services;

public sealed class WindowService : IWindowService
{
    private readonly INetworkSpeakerService _networkSpeakerService;
    private readonly IRoomTalkServerService _serverService;

    public WindowService(
        INetworkSpeakerService networkSpeakerService,
        IRoomTalkServerService serverService)
    {
        _networkSpeakerService = networkSpeakerService;
        _serverService = serverService;
    }

    public LoginDialogData? ShowLoginDialog(LoginDialogData currentData)
    {
        var viewModel = new LoginViewModel(currentData, _serverService);
        var window = new LoginWindow
        {
            Owner = Application.Current.MainWindow,
            DataContext = viewModel
        };

        viewModel.RequestClose += result => window.DialogResult = result;
        return window.ShowDialog() == true ? viewModel.Result : null;
    }

    public AudioSettingsData? ShowAudioSettingsDialog(AudioSettingsData currentSettings)
    {
        var viewModel = new AudioSettingsViewModel(
            new AudioDeviceService(),
            new NAudioTestService(),
            currentSettings);

        var window = new AudioSettingsWindow
        {
            Owner = Application.Current.MainWindow,
            DataContext = viewModel
        };

        viewModel.RequestClose += result => window.DialogResult = result;
        window.Closed += (_, _) => viewModel.Dispose();
        return window.ShowDialog() == true ? viewModel.Result : null;
    }

    public void ShowAdminWindow()
    {
        var viewModel = new AdminViewModel(_networkSpeakerService);
        var window = new AdminWindow
        {
            Owner = Application.Current.MainWindow,
            DataContext = viewModel
        };

        window.Closed += (_, _) => viewModel.Dispose();
        window.ShowDialog();
    }

    public void ShowMeetingWindow(string currentUsername)
    {
        var window = new MeetingWindow
        {
            Owner = Application.Current.MainWindow,
            CurrentUsername = currentUsername
        };

        window.ShowDialog();
    }

    public void ShowTestServerWindow()
    {
        var viewModel = new TestServerViewModel(_serverService);
        var window = new TestServerWindow
        {
            Owner = Application.Current.MainWindow,
            DataContext = viewModel
        };

        window.Closed += (_, _) => viewModel.Dispose();
        window.ShowDialog();
    }
}
