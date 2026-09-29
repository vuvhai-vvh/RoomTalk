using System.Windows;

namespace RoomTalk;

public partial class MeetingWindow : Window
{
    private string _currentUsername = "Người dùng";

    public MeetingWindow()
    {
        InitializeComponent();
        UpdateUsernameDisplay();
    }

    public string CurrentUsername
    {
        get => _currentUsername;
        set
        {
            _currentUsername = string.IsNullOrWhiteSpace(value)
                ? "Người dùng"
                : value;
            UpdateUsernameDisplay();
        }
    }

    private void UpdateUsernameDisplay()
    {
        if (UsernameText is null || ParticipantNameText is null)
        {
            return;
        }

        UsernameText.Text = $"Tài khoản: {_currentUsername}";
        ParticipantNameText.Text = _currentUsername;
    }

    private void LeaveMeeting_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
