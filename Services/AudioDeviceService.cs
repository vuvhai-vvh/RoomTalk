using NAudio.CoreAudioApi;
using RoomTalk.Models;

namespace RoomTalk.Services;

public sealed class AudioDeviceService : IAudioDeviceService
{
    public IReadOnlyList<AudioDeviceItem> GetInputDevices()
    {
        return EnumerateDevices(DataFlow.Capture);
    }

    public IReadOnlyList<AudioDeviceItem> GetOutputDevices()
    {
        return EnumerateDevices(DataFlow.Render);
    }

    private static IReadOnlyList<AudioDeviceItem> EnumerateDevices(DataFlow flow)
    {
        var result = new List<AudioDeviceItem>();

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultDeviceId = null;

            try
            {
                using var defaultDevice = enumerator.GetDefaultAudioEndpoint(flow, Role.Communications);
                defaultDeviceId = defaultDevice.ID;
            }
            catch
            {
                // Máy có thể chưa được cấu hình thiết bị mặc định.
            }

            var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
            foreach (var device in devices)
            {
                result.Add(new AudioDeviceItem
                {
                    Id = device.ID,
                    Name = device.FriendlyName,
                    IsDefault = string.Equals(device.ID, defaultDeviceId, StringComparison.OrdinalIgnoreCase)
                });
            }
        }
        catch
        {
            // Giao diện vẫn mở được nếu Windows không trả về danh sách thiết bị.
        }

        return result;
    }
}
