using RoomTalk.Models;

namespace RoomTalk.Services;

public interface IAudioDeviceService
{
    IReadOnlyList<AudioDeviceItem> GetInputDevices();
    IReadOnlyList<AudioDeviceItem> GetOutputDevices();
}
