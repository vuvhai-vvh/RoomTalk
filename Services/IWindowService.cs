using RoomTalk.Models;

namespace RoomTalk.Services;

public interface IWindowService
{
    LoginDialogData? ShowLoginDialog(LoginDialogData currentData);
    AudioSettingsData? ShowAudioSettingsDialog(AudioSettingsData currentSettings);
    void ShowAdminWindow();
    void ShowMeetingWindow(string currentUsername);
    void ShowTestServerWindow();
}
