using RoomTalk.Models;

namespace RoomTalk.Services;

public interface IAudioTestService : IDisposable
{
    Task<AudioTestResult> TestMicrophoneAsync(
        AudioSettingsData settings,
        TimeSpan recordDuration,
        IProgress<AudioTestProgress>? progress,
        CancellationToken cancellationToken);

    Task<AudioCalibrationResult> CalibrateMicrophoneAsync(
        AudioSettingsData settings,
        IProgress<AudioTestProgress>? progress,
        CancellationToken cancellationToken);

    Task<AudioTestResult> TestSpeakerAsync(
        AudioSettingsData settings,
        CancellationToken cancellationToken);

    void Stop();
}
