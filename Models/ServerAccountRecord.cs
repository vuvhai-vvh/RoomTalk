namespace RoomTalk.Models;

public sealed class ServerAccountRecord
{
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public AccountRole Role { get; set; }

    // Giữ lại để đọc được file cấu hình của các phiên bản cũ.
    // Từ V12, tên tài khoản chính là tên phòng/điểm liên lạc.
    public string AssignedRoom { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}
