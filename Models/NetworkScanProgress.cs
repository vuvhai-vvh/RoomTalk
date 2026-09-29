namespace RoomTalk.Models;

public sealed record NetworkScanProgress(
    int Completed,
    int Total,
    string CurrentIp,
    int Found)
{
    public int Percent => Total <= 0 ? 0 : (int)Math.Round(Completed * 100d / Total);
}
