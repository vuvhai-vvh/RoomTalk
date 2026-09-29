using System.Diagnostics;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RoomTalk.Models;

namespace RoomTalk.Services;

/// <summary>
/// Pipeline giao tiếp LAN V7:
/// PCM 48 kHz, 16-bit, mono, khung mạng 20 ms.
///
/// V7 ưu tiên độ rõ, độ trễ thấp và bán song công nghiêm ngặt:
/// - hàng đợi gửi ngắn, chỉ drain tối đa 90 ms để giữ âm cuối;
/// - không xóa buffer khi người nói vừa dừng để tránh cắt âm cuối;
/// - loa cục bộ dừng và chờ đuôi âm học trước khi mở mic;
/// - không dùng phép trừ echo tự chế làm méo giọng; loa cục bộ được đóng hẳn trước khi mở mic.
/// </summary>
public sealed class AudioCommunicationService : IAudioCommunicationService
{
    private const int SampleRate = 48000;
    private const int BitsPerSample = 16;
    private const int Channels = 1;
    private const int FrameDurationMilliseconds = 20;
    private const int BytesPerFrame =
        SampleRate * (BitsPerSample / 8) * Channels * FrameDurationMilliseconds / 1000;

    private const int SendQueueFrameCapacity = 3; // tối đa khoảng 60 ms
    private const int PlaybackLatencyMilliseconds = 30;
    private const int MaximumPlaybackBacklogMilliseconds = 80;
    private const int MinimumOutputDrainMilliseconds = 180;

    private static readonly WaveFormat NetworkWaveFormat =
        new(SampleRate, BitsPerSample, Channels);

    private readonly IRoomTalkClientService _clientService;
    private readonly RoomTalkVoiceProcessor _voiceProcessor = new(SampleRate);
    private readonly object _captureLock = new();
    private readonly object _playbackLock = new();

    private AudioSettingsData _settings = new();
    private WasapiCapture? _capture;
    private Channel<byte[]>? _sendChannel;
    private CancellationTokenSource? _captureCancellation;
    private Task? _sendTask;
    private byte[] _captureAccumulator = new byte[BytesPerFrame * 8];
    private int _captureAccumulatorCount;

    private BufferedWaveProvider? _playbackBuffer;
    private ProtectedOutputWaveProvider16? _playbackOutputProvider;
    private WasapiOut? _output;
    private long _lastRemoteAudioTimestamp;
    private long _lastMeterReportTimestamp;
    private double _displayedMicrophoneLevel;
    private volatile bool _isTalking;
    private volatile bool _isDuplexMode;
    private bool _disposed;

    public AudioCommunicationService(IRoomTalkClientService clientService)
    {
        _clientService = clientService;
        _clientService.AudioFrameReceived += ClientServiceOnAudioFrameReceived;
    }

    public event Action<double>? MicrophoneLevelChanged;

    public bool IsTalking => _isTalking;

    public void ApplySettings(AudioSettingsData settings)
    {
        ThrowIfDisposed();
        _settings = settings.Clone();

        lock (_playbackLock)
        {
            if (_playbackOutputProvider is not null)
            {
                _playbackOutputProvider.GainDb = GetSpeakerGainDb(_settings.SpeakerVolume);
            }
        }
    }

    public Task PreparePlaybackAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        EnsurePlaybackStarted();
        return Task.CompletedTask;
    }

    public Task StartTalkingAsync(CancellationToken cancellationToken = default)
    {
        return StartCaptureAsync(duplexMode: false, cancellationToken: cancellationToken);
    }

    public Task StartDuplexAsync(CancellationToken cancellationToken = default)
    {
        return StartCaptureAsync(duplexMode: true, cancellationToken: cancellationToken);
    }

    private async Task StartCaptureAsync(bool duplexMode, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (IsTalking)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.InputDeviceId))
        {
            throw new InvalidOperationException("Chưa chọn microphone. Hãy mở Cài đặt âm thanh.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var enumerator = new MMDeviceEnumerator();
        using var inputDevice = enumerator.GetDevice(_settings.InputDeviceId);

        var capture = new WasapiCapture(inputDevice)
        {
            ShareMode = AudioClientShareMode.Shared,
            WaveFormat = NetworkWaveFormat
        };

        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(SendQueueFrameCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        var captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        lock (_captureLock)
        {
            _captureAccumulatorCount = 0;
            _capture = capture;
            _sendChannel = channel;
            _captureCancellation = captureCancellation;
        }

        capture.DataAvailable += CaptureOnDataAvailable;
        capture.RecordingStopped += CaptureOnRecordingStopped;
        _sendTask = SendAudioLoopAsync(channel.Reader, captureCancellation.Token);

        try
        {
            _voiceProcessor.BeginStream(_settings.InputDeviceId, _settings);
            ResetMicrophoneMeter();

            _isDuplexMode = duplexMode;
            _isTalking = true;

            if (duplexMode)
            {
                EnsurePlaybackStarted();
            }
            else if (_settings.MuteLocalSpeakerWhileTalking)
            {
                SuspendLocalPlaybackForTalking();
                int guardMilliseconds = CalculateSpeakerTailGuardMilliseconds();
                await Task.Delay(guardMilliseconds, cancellationToken);
            }

            capture.StartRecording();
        }
        catch
        {
            _isTalking = false;
            _isDuplexMode = false;
            capture.DataAvailable -= CaptureOnDataAvailable;
            capture.RecordingStopped -= CaptureOnRecordingStopped;
            capture.Dispose();
            captureCancellation.Cancel();
            channel.Writer.TryComplete();
            captureCancellation.Dispose();

            lock (_captureLock)
            {
                _capture = null;
                _sendChannel = null;
                _captureCancellation = null;
                _sendTask = null;
            }

            ResumeLocalPlaybackAfterTalking();
            throw;
        }
    }

    public async Task StopTalkingAsync()
    {
        WasapiCapture? capture;
        Channel<byte[]>? channel;
        CancellationTokenSource? cancellation;
        Task? sendTask;

        bool wasDuplex;
        lock (_captureLock)
        {
            capture = _capture;
            channel = _sendChannel;
            cancellation = _captureCancellation;
            sendTask = _sendTask;
            wasDuplex = _isDuplexMode;
            _isTalking = false;
            _isDuplexMode = false;
        }

        if (capture is not null)
        {
            capture.DataAvailable -= CaptureOnDataAvailable;
            capture.RecordingStopped -= CaptureOnRecordingStopped;

            try
            {
                capture.StopRecording();
            }
            catch
            {
                // Thiết bị có thể đã tự dừng.
            }

            capture.Dispose();
        }

        // Gửi nốt phần rất ngắn đã thu được để không cắt mất phụ âm cuối câu.
        lock (_captureLock)
        {
            if (channel is not null && _captureAccumulatorCount > 0)
            {
                byte[] finalFrame = new byte[BytesPerFrame];
                Buffer.BlockCopy(
                    _captureAccumulator,
                    0,
                    finalFrame,
                    0,
                    Math.Min(_captureAccumulatorCount, BytesPerFrame));
                _voiceProcessor.Process(finalFrame, _settings);
                channel.Writer.TryWrite(finalFrame);
            }

            _capture = null;
            _sendChannel = null;
            _captureCancellation = null;
            _sendTask = null;
            _captureAccumulatorCount = 0;
        }

        // Chỉ drain tối đa khoảng 90 ms: đủ giữ âm cuối nhưng không tạo đuôi kéo dài.
        channel?.Writer.TryComplete();
        if (sendTask is not null)
        {
            try
            {
                await sendTask.WaitAsync(TimeSpan.FromMilliseconds(90));
            }
            catch
            {
                // Nếu mạng chậm, bỏ phần còn lại để không tích lũy âm thanh cũ.
            }
        }

        cancellation?.Cancel();
        cancellation?.Dispose();
        ResetMicrophoneMeter();
        if (!wasDuplex)
        {
            ResumeLocalPlaybackAfterTalking();
        }
    }

    public Task StopDuplexAsync() => StopTalkingAsync();

    public void FlushPlayback()
    {
        lock (_playbackLock)
        {
            _playbackBuffer?.ClearBuffer();
        }
    }

    public void StopPlayback()
    {
        lock (_playbackLock)
        {
            try
            {
                _output?.Stop();
            }
            catch
            {
                // Thiết bị có thể đã tự dừng.
            }

            _output?.Dispose();
            _output = null;
            _playbackOutputProvider = null;
            _playbackBuffer = null;
        }
    }

    private void CaptureOnDataAvailable(object? sender, WaveInEventArgs args)
    {
        VoiceProcessingMetrics? latestMetrics = null;

        lock (_captureLock)
        {
            Channel<byte[]>? channel = _sendChannel;
            if (channel is null || args.BytesRecorded <= 0 || !IsTalking)
            {
                return;
            }

            EnsureAccumulatorCapacity(_captureAccumulatorCount + args.BytesRecorded);
            Buffer.BlockCopy(
                args.Buffer,
                0,
                _captureAccumulator,
                _captureAccumulatorCount,
                args.BytesRecorded);
            _captureAccumulatorCount += args.BytesRecorded;

            while (_captureAccumulatorCount >= BytesPerFrame)
            {
                byte[] frame = new byte[BytesPerFrame];
                Buffer.BlockCopy(_captureAccumulator, 0, frame, 0, BytesPerFrame);
                latestMetrics = _voiceProcessor.Process(frame, _settings);
                channel.Writer.TryWrite(frame);

                int remaining = _captureAccumulatorCount - BytesPerFrame;
                if (remaining > 0)
                {
                    Buffer.BlockCopy(
                        _captureAccumulator,
                        BytesPerFrame,
                        _captureAccumulator,
                        0,
                        remaining);
                }

                _captureAccumulatorCount = remaining;
            }
        }

        if (latestMetrics is not null)
        {
            ReportMicrophoneLevel(latestMetrics.OutputPeakDbfs);
        }
    }

    private void CaptureOnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is not null)
        {
            _captureCancellation?.Cancel();
        }
    }

    private async Task SendAudioLoopAsync(
        ChannelReader<byte[]> reader,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (byte[] frame in reader.ReadAllAsync(cancellationToken))
            {
                await _clientService.SendAudioFrameAsync(frame, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Dừng nói bình thường; frame cũ bị bỏ.
        }
        catch
        {
            // Mất mạng sẽ được client service thông báo cho giao diện.
        }
    }

    private void ClientServiceOnAudioFrameReceived(byte[] audioFrame)
    {
        Interlocked.Exchange(ref _lastRemoteAudioTimestamp, Stopwatch.GetTimestamp());

        if (audioFrame.Length == 0 || (IsTalking && !_isDuplexMode))
        {
            return;
        }

        try
        {
            EnsurePlaybackStarted();
            lock (_playbackLock)
            {
                if (_playbackBuffer is null || (IsTalking && !_isDuplexMode))
                {
                    return;
                }

                int maximumBufferedBytes =
                    NetworkWaveFormat.AverageBytesPerSecond *
                    MaximumPlaybackBacklogMilliseconds / 1000;

                if (_playbackBuffer.BufferedBytes > maximumBufferedBytes)
                {
                    // Ưu tiên gói mới nhất, không phát câu cũ sau vài trăm mili giây.
                    _playbackBuffer.ClearBuffer();
                }

                _playbackBuffer.AddSamples(audioFrame, 0, audioFrame.Length);
            }
        }
        catch
        {
            // Không để lỗi thiết bị loa làm ngắt kết nối mạng.
        }
    }

    private void EnsurePlaybackStarted()
    {
        lock (_playbackLock)
        {
            if (_output is not null)
            {
                return;
            }

            using var enumerator = new MMDeviceEnumerator();
            using MMDevice outputDevice = string.IsNullOrWhiteSpace(_settings.OutputDeviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications)
                : enumerator.GetDevice(_settings.OutputDeviceId);

#pragma warning disable CS0618
            var output = new WasapiOut(
                outputDevice,
                AudioClientShareMode.Shared,
                true,
                PlaybackLatencyMilliseconds);
#pragma warning restore CS0618

            var playbackBuffer = new BufferedWaveProvider(NetworkWaveFormat)
            {
                BufferDuration = TimeSpan.FromMilliseconds(180),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };

            var outputProvider = new ProtectedOutputWaveProvider16(playbackBuffer)
            {
                GainDb = GetSpeakerGainDb(_settings.SpeakerVolume)
            };

            output.Init(outputProvider);
            output.Play();

            _playbackBuffer = playbackBuffer;
            _playbackOutputProvider = outputProvider;
            _output = output;
        }
    }

    private int CalculateSpeakerTailGuardMilliseconds()
    {
        int configuredGuard = Math.Clamp(
            _settings.SpeakerTailGuardMilliseconds,
            MinimumOutputDrainMilliseconds,
            700);

        long lastRemoteTimestamp = Interlocked.Read(ref _lastRemoteAudioTimestamp);
        if (lastRemoteTimestamp <= 0)
        {
            return MinimumOutputDrainMilliseconds;
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(lastRemoteTimestamp);
        int remainingFromLastRemoteFrame = configuredGuard -
                                           (int)Math.Clamp(
                                               elapsed.TotalMilliseconds,
                                               0,
                                               configuredGuard);

        return Math.Max(MinimumOutputDrainMilliseconds, remainingFromLastRemoteFrame);
    }

    private void SuspendLocalPlaybackForTalking()
    {
        lock (_playbackLock)
        {
            try
            {
                _output?.Stop();
            }
            catch
            {
                // Thiết bị có thể đã tự dừng.
            }

            _output?.Dispose();
            _output = null;
            _playbackOutputProvider = null;
            _playbackBuffer = null;
        }
    }

    private void ResumeLocalPlaybackAfterTalking()
    {
        if (!_settings.MuteLocalSpeakerWhileTalking || _disposed)
        {
            return;
        }

        try
        {
            EnsurePlaybackStarted();
        }
        catch
        {
            // Lỗi loa sẽ được báo khi có gói âm thanh tiếp theo hoặc khi mở cài đặt.
        }
    }


    private void ReportMicrophoneLevel(double peakDbfs)
    {
        double target = DbfsToMeterPercent(peakDbfs);
        _displayedMicrophoneLevel = target >= _displayedMicrophoneLevel
            ? _displayedMicrophoneLevel + (target - _displayedMicrophoneLevel) * 0.72
            : _displayedMicrophoneLevel + (target - _displayedMicrophoneLevel) * 0.22;

        long now = Stopwatch.GetTimestamp();
        long previous = Interlocked.Read(ref _lastMeterReportTimestamp);
        if (previous > 0 && Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds < 35)
        {
            return;
        }

        Interlocked.Exchange(ref _lastMeterReportTimestamp, now);
        MicrophoneLevelChanged?.Invoke(Math.Clamp(_displayedMicrophoneLevel, 0, 100));
    }

    private void ResetMicrophoneMeter()
    {
        _displayedMicrophoneLevel = 0;
        Interlocked.Exchange(ref _lastMeterReportTimestamp, 0);
        MicrophoneLevelChanged?.Invoke(0);
    }

    private static double DbfsToMeterPercent(double dbfs)
    {
        // -60 dBFS = im lặng, -6 dBFS = gần mức tối đa an toàn.
        return Math.Clamp((dbfs + 60.0) / 54.0 * 100.0, 0.0, 100.0);
    }

    private static double GetSpeakerGainDb(int speakerVolumePercent)
    {
        int clamped = Math.Clamp(speakerVolumePercent, 0, 120);
        if (clamped <= 0)
        {
            return -60.0;
        }

        if (clamped <= 100)
        {
            return 20.0 * Math.Log10(clamped / 100.0);
        }

        return (clamped - 100) / 20.0 * 3.0;
    }

    private void EnsureAccumulatorCapacity(int requiredLength)
    {
        if (_captureAccumulator.Length >= requiredLength)
        {
            return;
        }

        int newLength = Math.Max(requiredLength, _captureAccumulator.Length * 2);
        Array.Resize(ref _captureAccumulator, newLength);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AudioCommunicationService));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _clientService.AudioFrameReceived -= ClientServiceOnAudioFrameReceived;
        ResetMicrophoneMeter();
        await StopTalkingAsync();
        StopPlayback();
        GC.SuppressFinalize(this);
    }
}
