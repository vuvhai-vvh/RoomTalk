using System.IO;
using System.Text.Json;
using RoomTalk.Models;

namespace RoomTalk.Services;

/// <summary>
/// Lưu cấu hình âm thanh theo từng máy.
/// V8 giữ cấu hình giọng rõ của V7 và bổ sung đồng hồ mức mic cùng bài nghe thử ổn định hơn.
/// V7 đã đưa cấu hình V6 về mức an toàn vì bộ khử echo tương quan và chuỗi xử lý mạnh
/// có thể làm giọng mỏng, mất phụ âm và khó nghe rõ chữ.
/// </summary>
public static class AudioSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RoomTalk");

    private static string SettingsFile => Path.Combine(
        SettingsDirectory,
        "audio-settings.json");

    public static AudioSettingsData LoadOrDefault()
    {
        try
        {
            if (!File.Exists(SettingsFile))
            {
                return new AudioSettingsData();
            }

            string json = File.ReadAllText(SettingsFile);
            AudioSettingsData? settings = JsonSerializer.Deserialize<AudioSettingsData>(
                json,
                SerializerOptions);
            bool settingsRevisionWasStored = json.Contains(
                "\"SettingsRevision\"",
                StringComparison.OrdinalIgnoreCase);

            return Normalize(
                settings ?? new AudioSettingsData(),
                forceMigration: !settingsRevisionWasStored);
        }
        catch
        {
            return new AudioSettingsData();
        }
    }

    public static void Save(AudioSettingsData settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        AudioSettingsData normalized = Normalize(settings.Clone(), forceMigration: false);
        Directory.CreateDirectory(SettingsDirectory);
        string temporaryFile = SettingsFile + ".tmp";
        string json = JsonSerializer.Serialize(normalized, SerializerOptions);
        File.WriteAllText(temporaryFile, json);
        File.Move(temporaryFile, SettingsFile, true);
    }

    private static AudioSettingsData Normalize(
        AudioSettingsData settings,
        bool forceMigration)
    {
        bool migratedFromOlderVoiceEngine =
            forceMigration ||
            settings.SettingsRevision < AudioSettingsData.CurrentSettingsRevision;

        settings.SettingsRevision = AudioSettingsData.CurrentSettingsRevision;
        settings.ServerAddress = string.IsNullOrWhiteSpace(settings.ServerAddress)
            ? "127.0.0.1"
            : settings.ServerAddress.Trim();
        settings.ServerPort = Math.Clamp(settings.ServerPort, 1, 65535);
        settings.MicrophoneVolume = Math.Clamp(settings.MicrophoneVolume, 0, 100);
        settings.SpeakerVolume = Math.Clamp(settings.SpeakerVolume, 0, 120);
        settings.TargetSpeechLevelDbfs = Math.Clamp(settings.TargetSpeechLevelDbfs, -21.0, -17.0);
        settings.MaximumAgcGainDb = Math.Clamp(settings.MaximumAgcGainDb, 0.0, 10.0);
        settings.MaximumOutputNoiseLevelDbfs = Math.Clamp(
            settings.MaximumOutputNoiseLevelDbfs,
            -54.0,
            -46.0);
        settings.InputTrimDb = Math.Clamp(settings.InputTrimDb, -6.0, 6.0);
        settings.PreAmplifierEnabled = false;
        settings.PreAmplifierGain = 1.0;
        settings.NearbySpeakerEchoSuppressionEnabled = false;
        settings.EchoSuppressionStrength = 0.0;
        settings.SpeakerTailGuardMilliseconds = Math.Clamp(
            settings.SpeakerTailGuardMilliseconds,
            180,
            400);
        settings.MaximumCombinedInputGainDb = Math.Clamp(
            settings.MaximumCombinedInputGainDb,
            6.0,
            14.0);
        settings.MuteLocalSpeakerWhileTalking = true;
        settings.VoiceCompressorEnabled = false;
        settings.WebRtcApmEnabled = false;
        settings.EchoCancellation = false;

        if (migratedFromOlderVoiceEngine)
        {
            settings.ProcessingPreset = AudioProcessingPreset.Balanced;
            settings.NoiseSuppression = true;
            settings.NoiseSuppressionLevel = AudioNoiseSuppressionLevel.Moderate;
            settings.AutomaticGainControl = true;
            settings.HighPassFilter = true;
            settings.PeakLimiterEnabled = true;
            settings.InputTrimDb = 0.0;
            settings.MaximumAgcGainDb = 8.0;
            settings.MaximumCombinedInputGainDb = 12.0;
            settings.TargetSpeechLevelDbfs = -18.0;
            settings.MaximumOutputNoiseLevelDbfs = -48.0;
            settings.SpeakerTailGuardMilliseconds = 240;
            settings.CalibratedInputDeviceId = null;
            settings.LastCalibrationUtc = null;
        }

        return settings;
    }
}
