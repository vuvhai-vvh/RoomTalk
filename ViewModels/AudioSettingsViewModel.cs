using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoomTalk.Models;
using RoomTalk.Services;

namespace RoomTalk.ViewModels;

public sealed class AudioSettingsViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan MicrophoneTestDuration = TimeSpan.FromSeconds(4);

    private readonly IAudioDeviceService _audioDeviceService;
    private readonly IAudioTestService _audioTestService;
    private readonly AudioSettingsData _workingSettings;

    private AudioDeviceItem? _selectedInputDevice;
    private AudioDeviceItem? _selectedOutputDevice;
    private AudioPresetItem? _selectedPreset;
    private CancellationTokenSource? _testCancellation;
    private bool _isApplyingPreset;
    private bool _isTesting;
    private double _microphoneLevel;
    private string _statusMessage = "Chọn thiết bị, tự cân mic rồi ghi và nghe lại.";

    public AudioSettingsViewModel(
        IAudioDeviceService audioDeviceService,
        IAudioTestService audioTestService,
        AudioSettingsData currentSettings)
    {
        _audioDeviceService = audioDeviceService;
        _audioTestService = audioTestService;
        _workingSettings = currentSettings.Clone();

        Presets = new ObservableCollection<AudioPresetItem>
        {
            new()
            {
                Value = AudioProcessingPreset.Balanced,
                Name = "Giọng rõ (khuyên dùng)",
                Description = "Giữ nguyên phụ âm, cân âm lượng nhẹ và bảo vệ chống rè."
            },
            new()
            {
                Value = AudioProcessingPreset.StrongNoiseReduction,
                Name = "Phòng ồn",
                Description = "Giảm nền mạnh hơn một chút nhưng vẫn ưu tiên rõ chữ."
            },
            new()
            {
                Value = AudioProcessingPreset.Disabled,
                Name = "Âm thanh gốc (kiểm tra)",
                Description = "Tắt xử lý để so sánh trực tiếp với microphone."
            }
        };

        RefreshDevicesCommand = new RelayCommand(RefreshDevices, () => !IsTesting);
        SaveCommand = new RelayCommand(Save, () => !IsTesting);
        CancelCommand = new RelayCommand(Cancel);
        CalibrateMicrophoneCommand = new AsyncRelayCommand(
            CalibrateMicrophoneAsync,
            CanCalibrateMicrophone);
        TestMicrophoneCommand = new AsyncRelayCommand(TestMicrophoneAsync, CanTestMicrophone);
        TestSpeakerCommand = new AsyncRelayCommand(TestSpeakerAsync, CanTestSpeaker);

        _isApplyingPreset = true;
        SelectedPreset = Presets.FirstOrDefault(x => x.Value == _workingSettings.ProcessingPreset)
                         ?? Presets.First(x => x.Value == AudioProcessingPreset.Balanced);
        _isApplyingPreset = false;

        RefreshDevices();
    }

    public event Action<bool>? RequestClose;

    public AudioSettingsData? Result { get; private set; }

    public ObservableCollection<AudioDeviceItem> InputDevices { get; } = new();
    public ObservableCollection<AudioDeviceItem> OutputDevices { get; } = new();
    public ObservableCollection<AudioPresetItem> Presets { get; }

    public AudioDeviceItem? SelectedInputDevice
    {
        get => _selectedInputDevice;
        set
        {
            if (!SetProperty(ref _selectedInputDevice, value))
            {
                return;
            }

            CalibrateMicrophoneCommand.NotifyCanExecuteChanged();
            TestMicrophoneCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(IsCalibrationValid));
            OnPropertyChanged(nameof(CalibrationSummary));
        }
    }

    public AudioDeviceItem? SelectedOutputDevice
    {
        get => _selectedOutputDevice;
        set
        {
            if (!SetProperty(ref _selectedOutputDevice, value))
            {
                return;
            }

            TestMicrophoneCommand.NotifyCanExecuteChanged();
            TestSpeakerCommand.NotifyCanExecuteChanged();
        }
    }

    public AudioPresetItem? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!SetProperty(ref _selectedPreset, value) || value is null)
            {
                return;
            }

            OnPropertyChanged(nameof(PresetDescription));
            if (!_isApplyingPreset)
            {
                ApplyPreset(value.Value);
            }
        }
    }

    public string PresetDescription => SelectedPreset?.Description ?? string.Empty;

    public string ServerAddress
    {
        get => _workingSettings.ServerAddress;
        set
        {
            string normalized = value ?? string.Empty;
            if (_workingSettings.ServerAddress == normalized)
            {
                return;
            }

            _workingSettings.ServerAddress = normalized;
            OnPropertyChanged();
        }
    }

    public string ServerPort
    {
        get => _workingSettings.ServerPort.ToString();
        set
        {
            if (int.TryParse(value, out int port) && port is >= 1 and <= 65535)
            {
                _workingSettings.ServerPort = port;
            }
            OnPropertyChanged();
        }
    }

    public string ConnectionSummary =>
        $"Máy chủ đang lưu: {_workingSettings.ServerAddress}:{_workingSettings.ServerPort}";

    public int MicrophoneVolume
    {
        get => _workingSettings.MicrophoneVolume;
        set
        {
            int clamped = Math.Clamp(value, 0, 100);
            if (_workingSettings.MicrophoneVolume == clamped)
            {
                return;
            }

            _workingSettings.MicrophoneVolume = clamped;
            OnPropertyChanged();
        }
    }

    public int SpeakerVolume
    {
        get => _workingSettings.SpeakerVolume;
        set
        {
            int clamped = Math.Clamp(value, 0, 120);
            if (_workingSettings.SpeakerVolume == clamped)
            {
                return;
            }

            _workingSettings.SpeakerVolume = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SpeakerGainDescription));
        }
    }

    public string SpeakerGainDescription
    {
        get
        {
            if (SpeakerVolume <= 0)
            {
                return "Tắt";
            }

            double db = SpeakerVolume <= 100
                ? 20.0 * Math.Log10(SpeakerVolume / 100.0)
                : (SpeakerVolume - 100) / 20.0 * 3.0;
            return $"{(db >= 0 ? "+" : string.Empty)}{db:F1} dB";
        }
    }

    public bool IsCalibrationValid =>
        SelectedInputDevice is not null &&
        string.Equals(
            SelectedInputDevice.Id,
            _workingSettings.CalibratedInputDeviceId,
            StringComparison.OrdinalIgnoreCase);

    public string CalibrationSummary => IsCalibrationValid
        ? $"Đã cân mic · trim {FormatSignedDb(_workingSettings.InputTrimDb)}"
        : "Chưa cân microphone đang chọn";

    public bool IsTesting
    {
        get => _isTesting;
        private set
        {
            if (!SetProperty(ref _isTesting, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsNotTesting));
            RefreshDevicesCommand.NotifyCanExecuteChanged();
            SaveCommand.NotifyCanExecuteChanged();
            CalibrateMicrophoneCommand.NotifyCanExecuteChanged();
            TestMicrophoneCommand.NotifyCanExecuteChanged();
            TestSpeakerCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsNotTesting => !IsTesting;

    public double MicrophoneLevel
    {
        get => _microphoneLevel;
        private set => SetProperty(ref _microphoneLevel, Math.Clamp(value, 0d, 100d));
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public IRelayCommand RefreshDevicesCommand { get; }
    public IRelayCommand SaveCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand CalibrateMicrophoneCommand { get; }
    public IAsyncRelayCommand TestMicrophoneCommand { get; }
    public IAsyncRelayCommand TestSpeakerCommand { get; }

    private void RefreshDevices()
    {
        string? selectedInputId = SelectedInputDevice?.Id ?? _workingSettings.InputDeviceId;
        string? selectedOutputId = SelectedOutputDevice?.Id ?? _workingSettings.OutputDeviceId;

        InputDevices.Clear();
        foreach (AudioDeviceItem device in _audioDeviceService.GetInputDevices())
        {
            InputDevices.Add(device);
        }

        OutputDevices.Clear();
        foreach (AudioDeviceItem device in _audioDeviceService.GetOutputDevices())
        {
            OutputDevices.Add(device);
        }

        SelectedInputDevice = InputDevices.FirstOrDefault(x => x.Id == selectedInputId)
                              ?? InputDevices.FirstOrDefault(x => x.IsDefault)
                              ?? InputDevices.FirstOrDefault();

        SelectedOutputDevice = OutputDevices.FirstOrDefault(x => x.Id == selectedOutputId)
                               ?? OutputDevices.FirstOrDefault(x => x.IsDefault)
                               ?? OutputDevices.FirstOrDefault();

        StatusMessage = $"Đã tìm thấy {InputDevices.Count} mic và {OutputDevices.Count} thiết bị phát.";
    }

    private bool CanCalibrateMicrophone()
    {
        return !IsTesting && SelectedInputDevice is not null;
    }

    private bool CanTestMicrophone()
    {
        return !IsTesting && SelectedInputDevice is not null && SelectedOutputDevice is not null;
    }

    private bool CanTestSpeaker()
    {
        return !IsTesting && SelectedOutputDevice is not null;
    }

    private async Task CalibrateMicrophoneAsync()
    {
        UpdateSelectedDevices();
        BeginTest();

        try
        {
            var progress = new Progress<AudioTestProgress>(update =>
            {
                StatusMessage = update.Message;
                MicrophoneLevel = update.MicrophoneLevel;
            });

            AudioCalibrationResult result = await _audioTestService.CalibrateMicrophoneAsync(
                _workingSettings,
                progress,
                _testCancellation!.Token);

            StatusMessage = result.Message;
            if (!result.IsSuccess || SelectedInputDevice is null)
            {
                return;
            }

            _workingSettings.InputTrimDb = result.InputTrimDb;
            _workingSettings.CalibratedNoiseFloorDbfs = result.NoiseFloorDbfs;
            _workingSettings.CalibratedSpeechLevelDbfs = result.SpeechLevelDbfs;
            _workingSettings.CalibratedPeakDbfs = result.PeakDbfs;
            _workingSettings.MaximumAgcGainDb = result.SuggestedMaximumAgcGainDb;
            _workingSettings.CalibratedInputDeviceId = SelectedInputDevice.Id;
            _workingSettings.LastCalibrationUtc = DateTimeOffset.UtcNow;

            OnPropertyChanged(nameof(IsCalibrationValid));
            OnPropertyChanged(nameof(CalibrationSummary));
            StatusMessage = "Đã cân mic. Bấm Ghi và nghe lại để kiểm tra.";
        }
        finally
        {
            EndTest();
        }
    }

    private async Task TestMicrophoneAsync()
    {
        UpdateSelectedDevices();
        BeginTest();

        try
        {
            var progress = new Progress<AudioTestProgress>(update =>
            {
                StatusMessage = update.Message;
                MicrophoneLevel = update.MicrophoneLevel;
            });

            AudioTestResult result = await _audioTestService.TestMicrophoneAsync(
                _workingSettings,
                MicrophoneTestDuration,
                progress,
                _testCancellation!.Token);

            StatusMessage = result.Message;
        }
        finally
        {
            EndTest();
        }
    }

    private async Task TestSpeakerAsync()
    {
        UpdateSelectedDevices();
        BeginTest();
        StatusMessage = "Đang thử loa...";

        try
        {
            AudioTestResult result = await _audioTestService.TestSpeakerAsync(
                _workingSettings,
                _testCancellation!.Token);
            StatusMessage = result.Message;
        }
        finally
        {
            EndTest();
        }
    }

    private void BeginTest()
    {
        _testCancellation?.Dispose();
        _testCancellation = new CancellationTokenSource();
        MicrophoneLevel = 0;
        IsTesting = true;
    }

    private void EndTest()
    {
        IsTesting = false;
        MicrophoneLevel = 0;
        _testCancellation?.Dispose();
        _testCancellation = null;
    }

    private void CancelActiveTest()
    {
        _testCancellation?.Cancel();
        _audioTestService.Stop();
    }

    private void ApplyPreset(AudioProcessingPreset preset)
    {
        _isApplyingPreset = true;
        try
        {
            _workingSettings.ProcessingPreset = preset;
            _workingSettings.WebRtcApmEnabled = false;
            _workingSettings.EchoCancellation = false;
            _workingSettings.PreAmplifierEnabled = false;
            _workingSettings.PreAmplifierGain = 1.0;
            _workingSettings.NearbySpeakerEchoSuppressionEnabled = false;
            _workingSettings.EchoSuppressionStrength = 0.0;
            _workingSettings.MuteLocalSpeakerWhileTalking = true;
            _workingSettings.VoiceCompressorEnabled = false;

            switch (preset)
            {
                case AudioProcessingPreset.StrongNoiseReduction:
                    _workingSettings.HighPassFilter = true;
                    _workingSettings.NoiseSuppression = true;
                    _workingSettings.NoiseSuppressionLevel = AudioNoiseSuppressionLevel.High;
                    _workingSettings.AutomaticGainControl = true;
                    _workingSettings.PeakLimiterEnabled = true;
                    _workingSettings.TargetSpeechLevelDbfs = -18.5;
                    _workingSettings.MaximumAgcGainDb = Math.Min(
                        Math.Max(_workingSettings.MaximumAgcGainDb, 4.0),
                        6.0);
                    _workingSettings.MaximumOutputNoiseLevelDbfs = -50.0;
                    _workingSettings.MaximumCombinedInputGainDb = 10.0;
                    _workingSettings.SpeakerTailGuardMilliseconds = 280;
                    break;

                case AudioProcessingPreset.Disabled:
                    _workingSettings.HighPassFilter = false;
                    _workingSettings.NoiseSuppression = false;
                    _workingSettings.AutomaticGainControl = false;
                    _workingSettings.PeakLimiterEnabled = false;
                    _workingSettings.TargetSpeechLevelDbfs = -18.0;
                    _workingSettings.MaximumAgcGainDb = 0.0;
                    _workingSettings.MaximumCombinedInputGainDb = 6.0;
                    _workingSettings.SpeakerTailGuardMilliseconds = 240;
                    break;

                default:
                    _workingSettings.ProcessingPreset = AudioProcessingPreset.Balanced;
                    _workingSettings.HighPassFilter = true;
                    _workingSettings.NoiseSuppression = true;
                    _workingSettings.NoiseSuppressionLevel = AudioNoiseSuppressionLevel.Moderate;
                    _workingSettings.AutomaticGainControl = true;
                    _workingSettings.PeakLimiterEnabled = true;
                    _workingSettings.TargetSpeechLevelDbfs = -18.0;
                    _workingSettings.MaximumAgcGainDb = Math.Min(
                        Math.Max(_workingSettings.MaximumAgcGainDb, 4.0),
                        8.0);
                    _workingSettings.MaximumOutputNoiseLevelDbfs = -48.0;
                    _workingSettings.MaximumCombinedInputGainDb = 12.0;
                    _workingSettings.SpeakerTailGuardMilliseconds = 240;
                    break;
            }
        }
        finally
        {
            _isApplyingPreset = false;
        }

        OnPropertyChanged(nameof(PresetDescription));
        StatusMessage = $"Đã chọn: {SelectedPreset?.Name}.";
    }

    private void UpdateSelectedDevices()
    {
        _workingSettings.InputDeviceId = SelectedInputDevice?.Id;
        _workingSettings.InputDeviceName = SelectedInputDevice?.Name;
        _workingSettings.OutputDeviceId = SelectedOutputDevice?.Id;
        _workingSettings.OutputDeviceName = SelectedOutputDevice?.Name;
    }

    private void Save()
    {
        UpdateSelectedDevices();
        _workingSettings.SettingsRevision = AudioSettingsData.CurrentSettingsRevision;
        Result = _workingSettings.Clone();
        RequestClose?.Invoke(true);
    }

    private void Cancel()
    {
        CancelActiveTest();
        RequestClose?.Invoke(false);
    }

    private static string FormatSignedDb(double value)
    {
        return $"{(value >= 0 ? "+" : string.Empty)}{value:F1} dB";
    }

    public void Dispose()
    {
        CancelActiveTest();
        _testCancellation?.Dispose();
        _audioTestService.Dispose();
        GC.SuppressFinalize(this);
    }
}
