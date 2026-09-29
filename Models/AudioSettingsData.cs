namespace RoomTalk.Models;

public sealed class AudioSettingsData
{
    public const int CurrentSettingsRevision = 8;

    public int SettingsRevision { get; set; } = CurrentSettingsRevision;
    public string? InputDeviceId { get; set; }
    public string? OutputDeviceId { get; set; }
    public string? InputDeviceName { get; set; }
    public string? OutputDeviceName { get; set; }

    // Địa chỉ đăng nhập gần nhất được lưu để tự điền ở lần đăng nhập tiếp theo.
    public string ServerAddress { get; set; } = "127.0.0.1";
    public int ServerPort { get; set; } = 5000;

    /// <summary>
    /// Điều chỉnh thủ công trước DSP. 100% = không giảm tín hiệu.
    /// </summary>
    public int MicrophoneVolume { get; set; } = 100;

    /// <summary>
    /// 100% = 0 dB. Vùng 101-120% tăng nhẹ tối đa khoảng +3 dB
    /// và luôn đi qua limiter bảo vệ đầu ra.
    /// </summary>
    public int SpeakerVolume { get; set; } = 100;

    public AudioProcessingPreset ProcessingPreset { get; set; } = AudioProcessingPreset.Balanced;

    // Để dành cho chế độ họp song công. Chế độ bấm để nói hiện tại không dùng AEC3.
    public bool WebRtcApmEnabled { get; set; }
    public bool EchoCancellation { get; set; }
    public int EchoCancellationLatencyMs { get; set; } = 60;

    public bool NoiseSuppression { get; set; } = true;
    public AudioNoiseSuppressionLevel NoiseSuppressionLevel { get; set; } = AudioNoiseSuppressionLevel.Moderate;
    public bool AutomaticGainControl { get; set; } = true;
    public bool HighPassFilter { get; set; } = true;
    public bool VoiceCompressorEnabled { get; set; } = false;
    public bool PeakLimiterEnabled { get; set; } = true;

    public bool PreAmplifierEnabled { get; set; }
    public double PreAmplifierGain { get; set; } = 1.0;

    // Chế độ bán song công: dừng loa cục bộ trước khi mở mic.
    public bool MuteLocalSpeakerWhileTalking { get; set; } = true;

    // Thuộc tính cũ được giữ để đọc file cấu hình V6; V7 không dùng phép trừ echo tự chế.
    public bool NearbySpeakerEchoSuppressionEnabled { get; set; } = false;
    public double EchoSuppressionStrength { get; set; } = 0.0;

    /// <summary>
    /// Thời gian chờ sau khi dừng loa trước khi microphone bắt đầu thu.
    /// Giá trị dài hơn hữu ích khi các thiết bị đặt gần nhau hoặc phòng có nhiều dội âm.
    /// </summary>
    public int SpeakerTailGuardMilliseconds { get; set; } = 240;

    /// <summary>
    /// Giới hạn tổng gain dương của input trim + AGC để giữ giọng tự nhiên và hạn chế nâng tiếng nền.
    /// </summary>
    public double MaximumCombinedInputGainDb { get; set; } = 12.0;

    // Chuẩn âm thanh nội bộ của RoomTalk.
    public int SampleRate { get; set; } = 48000;
    public int Channels { get; set; } = 1;

    // Các tham số của RoomTalk Voice Engine V7.
    public double TargetSpeechLevelDbfs { get; set; } = -18.0;
    public double MaximumAgcGainDb { get; set; } = 8.0;
    public double MaximumOutputNoiseLevelDbfs { get; set; } = -48.0;
    public double InputTrimDb { get; set; }
    public double CalibratedNoiseFloorDbfs { get; set; } = -60.0;
    public double CalibratedSpeechLevelDbfs { get; set; } = -30.0;
    public double CalibratedPeakDbfs { get; set; } = -12.0;
    public string? CalibratedInputDeviceId { get; set; }
    public DateTimeOffset? LastCalibrationUtc { get; set; }

    // Dành cho giai đoạn sau. Bản hiện tại không tải mô hình AI bên ngoài.
    public bool AiNoiseReductionPlanned { get; set; }

    public bool IsCalibrationValidForCurrentInput =>
        !string.IsNullOrWhiteSpace(InputDeviceId) &&
        string.Equals(
            InputDeviceId,
            CalibratedInputDeviceId,
            StringComparison.OrdinalIgnoreCase);

    public AudioSettingsData Clone()
    {
        return new AudioSettingsData
        {
            SettingsRevision = SettingsRevision,
            InputDeviceId = InputDeviceId,
            OutputDeviceId = OutputDeviceId,
            InputDeviceName = InputDeviceName,
            OutputDeviceName = OutputDeviceName,
            ServerAddress = ServerAddress,
            ServerPort = ServerPort,
            MicrophoneVolume = MicrophoneVolume,
            SpeakerVolume = SpeakerVolume,
            ProcessingPreset = ProcessingPreset,
            WebRtcApmEnabled = WebRtcApmEnabled,
            EchoCancellation = EchoCancellation,
            EchoCancellationLatencyMs = EchoCancellationLatencyMs,
            NoiseSuppression = NoiseSuppression,
            NoiseSuppressionLevel = NoiseSuppressionLevel,
            AutomaticGainControl = AutomaticGainControl,
            HighPassFilter = HighPassFilter,
            VoiceCompressorEnabled = VoiceCompressorEnabled,
            PeakLimiterEnabled = PeakLimiterEnabled,
            PreAmplifierEnabled = PreAmplifierEnabled,
            PreAmplifierGain = PreAmplifierGain,
            MuteLocalSpeakerWhileTalking = MuteLocalSpeakerWhileTalking,
            NearbySpeakerEchoSuppressionEnabled = NearbySpeakerEchoSuppressionEnabled,
            EchoSuppressionStrength = EchoSuppressionStrength,
            SpeakerTailGuardMilliseconds = SpeakerTailGuardMilliseconds,
            MaximumCombinedInputGainDb = MaximumCombinedInputGainDb,
            SampleRate = SampleRate,
            Channels = Channels,
            TargetSpeechLevelDbfs = TargetSpeechLevelDbfs,
            MaximumAgcGainDb = MaximumAgcGainDb,
            MaximumOutputNoiseLevelDbfs = MaximumOutputNoiseLevelDbfs,
            InputTrimDb = InputTrimDb,
            CalibratedNoiseFloorDbfs = CalibratedNoiseFloorDbfs,
            CalibratedSpeechLevelDbfs = CalibratedSpeechLevelDbfs,
            CalibratedPeakDbfs = CalibratedPeakDbfs,
            CalibratedInputDeviceId = CalibratedInputDeviceId,
            LastCalibrationUtc = LastCalibrationUtc,
            AiNoiseReductionPlanned = AiNoiseReductionPlanned
        };
    }
}
