using RoomTalk.Models;

namespace RoomTalk.Services;

public interface IAudioCommunicationService : IAsyncDisposable
{
    event Action<double>? MicrophoneLevelChanged;

    bool IsTalking { get; }
    void ApplySettings(AudioSettingsData settings);
    Task PreparePlaybackAsync(CancellationToken cancellationToken = default);
    Task StartTalkingAsync(CancellationToken cancellationToken = default);
    Task StartDuplexAsync(CancellationToken cancellationToken = default);
    Task StopTalkingAsync();
    Task StopDuplexAsync();
    void FlushPlayback();
    void StopPlayback();
}
