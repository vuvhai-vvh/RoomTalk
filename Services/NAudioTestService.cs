using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RoomTalk.Models;

namespace RoomTalk.Services;

/// <summary>
/// Kiểm tra và hiệu chỉnh phần cứng âm thanh bằng đúng pipeline đang dùng khi giao tiếp.
/// </summary>
public sealed class NAudioTestService : IAudioTestService
{
    private const int SampleRate = 48000;
    private const int BitsPerSample = 16;
    private const int Channels = 1;
    private const int FrameDurationMilliseconds = 20;
    private const int BytesPerFrame =
        SampleRate * (BitsPerSample / 8) * Channels * FrameDurationMilliseconds / 1000;
    private const int CalibrationNoiseSeconds = 2;
    private const int CalibrationSpeechSeconds = 4;

    private static readonly WaveFormat NetworkWaveFormat =
        new(SampleRate, BitsPerSample, Channels);

    private readonly object _syncRoot = new();
    private WasapiCapture? _activeCapture;
    private WasapiOut? _activeOutput;
    private bool _disposed;

    public async Task<AudioTestResult> TestMicrophoneAsync(
        AudioSettingsData settings,
        TimeSpan recordDuration,
        IProgress<AudioTestProgress>? progress,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(settings.InputDeviceId))
        {
            return AudioTestResult.Failure("Chưa chọn microphone.");
        }

        if (string.IsNullOrWhiteSpace(settings.OutputDeviceId))
        {
            return AudioTestResult.Failure("Chưa chọn loa hoặc tai nghe để phát lại.");
        }

        if (settings.SpeakerVolume <= 0)
        {
            return AudioTestResult.Failure("Mức phát RoomTalk đang là 0%. Hãy tăng mức phát rồi thử lại.");
        }

        try
        {
            progress?.Report(new AudioTestProgress(
                $"Nói bình thường trong {recordDuration.TotalSeconds:0} giây...",
                0));

            CapturedMicrophoneAudio captured = await RecordProcessedMicrophoneAsync(
                settings,
                recordDuration,
                progress,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            if (captured.PcmBytes.Length < BytesPerFrame ||
                captured.PeakDbfs < -55.0 ||
                captured.RmsDbfs < -65.0)
            {
                return AudioTestResult.Failure(
                    "Microphone gần như không có tín hiệu. Kiểm tra đúng thiết bị và quyền microphone của Windows.");
            }

            // Nghe thử phải nghe được ngay cả khi microphone đang đặt mức thấp.
            // Chỉ phần nghe thử được bù tối đa +18 dB; luồng giao tiếp vẫn dùng cấu hình đã lưu.
            double previewBoostDb = Math.Clamp(-8.0 - captured.PeakDbfs, 0.0, 18.0);
            progress?.Report(new AudioTestProgress(
                "Đã ghi xong. Đang phát lại qua loa đã chọn...",
                0));

            await Task.Delay(180, cancellationToken);
            await PlayPcmBytesAsync(
                settings.OutputDeviceId,
                captured.PcmBytes,
                settings.SpeakerVolume,
                previewBoostDb,
                cancellationToken);

            string boostNote = previewBoostDb >= 0.5
                ? $" (nghe thử được bù +{previewBoostDb:F1} dB vì tín hiệu mic nhỏ)"
                : string.Empty;

            progress?.Report(new AudioTestProgress("Đã phát lại giọng vừa ghi.", 0));
            return AudioTestResult.Success($"Đã phát lại giọng vừa ghi{boostNote}.");
        }
        catch (OperationCanceledException)
        {
            return AudioTestResult.Failure("Đã hủy kiểm tra microphone.");
        }
        catch (Exception ex)
        {
            return AudioTestResult.Failure($"Không thể kiểm tra microphone: {ex.Message}");
        }
        finally
        {
            Stop();
        }
    }

    public async Task<AudioCalibrationResult> CalibrateMicrophoneAsync(
        AudioSettingsData settings,
        IProgress<AudioTestProgress>? progress,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(settings.InputDeviceId))
        {
            return AudioCalibrationResult.Failure("Chưa chọn microphone.");
        }

        try
        {
            var noiseLevels = new List<double>();
            var speechLevels = new List<double>();
            var speechPeaks = new List<double>();

            await CaptureCalibrationDataAsync(
                settings.InputDeviceId,
                noiseLevels,
                speechLevels,
                speechPeaks,
                progress,
                cancellationToken);

            if (noiseLevels.Count < 40)
            {
                return AudioCalibrationResult.Failure(
                    "Không thu đủ dữ liệu nền. Hãy kiểm tra microphone và thử lại.");
            }

            double noiseFloorDbfs = Percentile(noiseLevels, 0.65);
            double activeThresholdDbfs = Math.Max(noiseFloorDbfs + 10.0, -52.0);
            List<double> activeSpeechLevels = speechLevels
                .Where(level => level > activeThresholdDbfs)
                .ToList();

            if (activeSpeechLevels.Count < 35)
            {
                return AudioCalibrationResult.Failure(
                    "Không phát hiện đủ giọng nói. Hãy nói liên tục bằng giọng bình thường.");
            }

            double speechLevelDbfs = Percentile(activeSpeechLevels, 0.60);
            List<double> activePeaks = speechPeaks
                .Where((_, index) => index < speechLevels.Count && speechLevels[index] > activeThresholdDbfs)
                .ToList();
            double peakDbfs = activePeaks.Count > 0
                ? Percentile(activePeaks, 0.95)
                : -12.0;

            // V7 chỉ dùng trim nhẹ để giữ chất giọng tự nhiên. Phần còn thiếu do AGC chậm xử lý.
            double desiredTrimDb = -24.0 - speechLevelDbfs;
            double maximumTrimFromNoiseDb = -48.0 - noiseFloorDbfs;
            double maximumTrimFromPeakDb = -6.0 - peakDbfs;
            double safeMaximumTrimDb = Math.Clamp(
                Math.Min(6.0, Math.Min(maximumTrimFromNoiseDb, maximumTrimFromPeakDb)),
                -6.0,
                6.0);
            double inputTrimDb = Math.Clamp(desiredTrimDb, -6.0, safeMaximumTrimDb);

            double snrDb = speechLevelDbfs - noiseFloorDbfs;
            double maximumAgcGainDb = snrDb switch
            {
                >= 28.0 => 8.0,
                >= 22.0 => 7.0,
                >= 16.0 => 6.0,
                _ => 4.0
            };

            double minimumNeededGain = Math.Max(
                3.0,
                settings.TargetSpeechLevelDbfs - (speechLevelDbfs + inputTrimDb) + 1.0);
            maximumAgcGainDb = Math.Clamp(
                Math.Max(maximumAgcGainDb, minimumNeededGain),
                3.0,
                8.0);

            maximumAgcGainDb = Math.Min(
                maximumAgcGainDb,
                Math.Max(0.0, settings.MaximumCombinedInputGainDb - Math.Max(0.0, inputTrimDb)));

            string message =
                $"Đã cân mic: trim {FormatSignedDb(inputTrimDb)}, AGC tối đa {maximumAgcGainDb:F0} dB.";

            return new AudioCalibrationResult(
                true,
                message,
                inputTrimDb,
                noiseFloorDbfs,
                speechLevelDbfs,
                peakDbfs,
                maximumAgcGainDb);
        }
        catch (OperationCanceledException)
        {
            return AudioCalibrationResult.Failure("Đã hủy hiệu chỉnh microphone.");
        }
        catch (Exception ex)
        {
            return AudioCalibrationResult.Failure($"Không thể hiệu chỉnh microphone: {ex.Message}");
        }
        finally
        {
            Stop();
        }
    }

    public async Task<AudioTestResult> TestSpeakerAsync(
        AudioSettingsData settings,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(settings.OutputDeviceId))
        {
            return AudioTestResult.Failure("Chưa chọn loa hoặc tai nghe.");
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var outputDevice = enumerator.GetDevice(settings.OutputDeviceId);

#pragma warning disable CS0618
            using var output = new WasapiOut(
                outputDevice,
                AudioClientShareMode.Shared,
                true,
                80);
#pragma warning restore CS0618

            SetActiveOutput(output);
            var chime = new RoomTalkChimeSampleProvider();
            var pcmProvider = new SampleToWaveProvider16(chime);
            var protectedOutput = new ProtectedOutputWaveProvider16(pcmProvider)
            {
                GainDb = GetSpeakerGainDb(settings.SpeakerVolume)
            };

            output.Init(protectedOutput);
            await PlayAndWaitAsync(output, cancellationToken);
            return AudioTestResult.Success("Đã phát âm báo qua loa được chọn.");
        }
        catch (OperationCanceledException)
        {
            return AudioTestResult.Failure("Đã hủy kiểm tra loa.");
        }
        catch (Exception ex)
        {
            return AudioTestResult.Failure($"Không thể kiểm tra loa: {ex.Message}");
        }
        finally
        {
            ClearActiveOutput();
        }
    }

    public void Stop()
    {
        lock (_syncRoot)
        {
            try
            {
                _activeCapture?.StopRecording();
            }
            catch
            {
                // Thiết bị có thể đã tự dừng.
            }

            try
            {
                _activeOutput?.Stop();
            }
            catch
            {
                // Thiết bị có thể đã tự dừng.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }

    private async Task<CapturedMicrophoneAudio> RecordProcessedMicrophoneAsync(
        AudioSettingsData settings,
        TimeSpan duration,
        IProgress<AudioTestProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var inputDevice = enumerator.GetDevice(settings.InputDeviceId!);
        using var capture = new WasapiCapture(inputDevice)
        {
            ShareMode = AudioClientShareMode.Shared,
            WaveFormat = NetworkWaveFormat
        };
        using var audioBuffer = new MemoryStream();

        SetActiveCapture(capture);
        var processor = new RoomTalkVoiceProcessor(SampleRate);
        processor.BeginStream(settings.InputDeviceId, settings);

        var stopped = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var accumulator = new byte[BytesPerFrame * 8];
        int accumulatorCount = 0;
        object bufferLock = new();
        var progressThrottle = Stopwatch.StartNew();
        Exception? callbackError = null;
        double sumSquares = 0;
        double peak = 0;
        long sampleCount = 0;

        void ProcessAndStoreFrame(byte[] frame)
        {
            VoiceProcessingMetrics metrics = processor.Process(frame, settings);
            audioBuffer.Write(frame, 0, frame.Length);
            AccumulatePcm16(frame, ref sumSquares, ref peak, ref sampleCount);

            if (progressThrottle.ElapsedMilliseconds >= 45)
            {
                progressThrottle.Restart();
                progress?.Report(new AudioTestProgress(
                    $"Đang ghi · mức mic {metrics.OutputPeakDbfs:F0} dBFS",
                    DbfsToPercent(metrics.OutputPeakDbfs)));
            }
        }

        capture.DataAvailable += (_, args) =>
        {
            lock (bufferLock)
            {
                if (callbackError is not null)
                {
                    return;
                }

                try
                {
                    EnsureCapacity(ref accumulator, accumulatorCount + args.BytesRecorded);
                    Buffer.BlockCopy(args.Buffer, 0, accumulator, accumulatorCount, args.BytesRecorded);
                    accumulatorCount += args.BytesRecorded;

                    while (accumulatorCount >= BytesPerFrame)
                    {
                        byte[] frame = new byte[BytesPerFrame];
                        Buffer.BlockCopy(accumulator, 0, frame, 0, BytesPerFrame);
                        ProcessAndStoreFrame(frame);

                        int remaining = accumulatorCount - BytesPerFrame;
                        if (remaining > 0)
                        {
                            Buffer.BlockCopy(accumulator, BytesPerFrame, accumulator, 0, remaining);
                        }

                        accumulatorCount = remaining;
                    }
                }
                catch (Exception ex)
                {
                    callbackError = ex;
                }
            }
        };

        capture.RecordingStopped += (_, args) => stopped.TrySetResult(args.Exception);
        capture.StartRecording();

        bool wasCancelled = false;
        try
        {
            await Task.Delay(duration, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
        }
        finally
        {
            try
            {
                capture.StopRecording();
            }
            catch
            {
                // RecordingStopped có thể đã xảy ra do thiết bị bị rút.
            }
        }

        Exception? stopError = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(4));
        byte[] pcmBytes;
        lock (bufferLock)
        {
            if (callbackError is null && accumulatorCount > 0)
            {
                byte[] finalFrame = new byte[BytesPerFrame];
                int copyCount = Math.Min(accumulatorCount, BytesPerFrame);
                Buffer.BlockCopy(accumulator, 0, finalFrame, 0, copyCount);
                ProcessAndStoreFrame(finalFrame);
            }

            pcmBytes = audioBuffer.ToArray();
        }
        ClearActiveCapture();

        if (wasCancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (callbackError is not null)
        {
            throw new InvalidOperationException(callbackError.Message, callbackError);
        }

        if (stopError is not null)
        {
            throw new InvalidOperationException(stopError.Message, stopError);
        }

        if (pcmBytes.Length < BytesPerFrame || sampleCount <= 0)
        {
            throw new InvalidOperationException("Không thu được dữ liệu từ microphone.");
        }

        return new CapturedMicrophoneAudio(
            pcmBytes,
            LinearToDb(peak),
            LinearToDb(Math.Sqrt(sumSquares / sampleCount)));
    }

    private async Task CaptureCalibrationDataAsync(
        string deviceId,
        List<double> noiseLevels,
        List<double> speechLevels,
        List<double> speechPeaks,
        IProgress<AudioTestProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var inputDevice = enumerator.GetDevice(deviceId);
        using var capture = new WasapiCapture(inputDevice)
        {
            ShareMode = AudioClientShareMode.Shared,
            WaveFormat = NetworkWaveFormat
        };

        SetActiveCapture(capture);
        var stopped = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var accumulator = new byte[BytesPerFrame * 8];
        int accumulatorCount = 0;
        int frameIndex = 0;
        int noiseFrameCount = CalibrationNoiseSeconds * 1000 / FrameDurationMilliseconds;
        int totalFrameCount =
            (CalibrationNoiseSeconds + CalibrationSpeechSeconds) *
            1000 / FrameDurationMilliseconds;
        object dataLock = new();

        capture.DataAvailable += (_, args) =>
        {
            lock (dataLock)
            {
                EnsureCapacity(ref accumulator, accumulatorCount + args.BytesRecorded);
                Buffer.BlockCopy(args.Buffer, 0, accumulator, accumulatorCount, args.BytesRecorded);
                accumulatorCount += args.BytesRecorded;

                while (accumulatorCount >= BytesPerFrame && frameIndex < totalFrameCount)
                {
                    (double rmsDbfs, double peakDbfs) = CalculatePcm16Levels(
                        accumulator,
                        0,
                        BytesPerFrame);

                    if (frameIndex < noiseFrameCount)
                    {
                        noiseLevels.Add(rmsDbfs);
                        if (frameIndex % 5 == 0)
                        {
                            progress?.Report(new AudioTestProgress(
                                $"Giữ im lặng... còn {(noiseFrameCount - frameIndex) * FrameDurationMilliseconds / 1000.0:F1} giây",
                                DbfsToPercent(peakDbfs)));
                        }
                    }
                    else
                    {
                        speechLevels.Add(rmsDbfs);
                        speechPeaks.Add(peakDbfs);
                        if (frameIndex % 5 == 0)
                        {
                            progress?.Report(new AudioTestProgress(
                                $"Nói bình thường... còn {(totalFrameCount - frameIndex) * FrameDurationMilliseconds / 1000.0:F1} giây",
                                DbfsToPercent(peakDbfs)));
                        }
                    }

                    int remaining = accumulatorCount - BytesPerFrame;
                    if (remaining > 0)
                    {
                        Buffer.BlockCopy(accumulator, BytesPerFrame, accumulator, 0, remaining);
                    }

                    accumulatorCount = remaining;
                    frameIndex++;
                }
            }
        };

        capture.RecordingStopped += (_, args) => stopped.TrySetResult(args.Exception);
        progress?.Report(new AudioTestProgress(
            "Giữ im lặng 2 giây..."));
        capture.StartRecording();

        bool wasCancelled = false;
        try
        {
            await Task.Delay(
                TimeSpan.FromSeconds(CalibrationNoiseSeconds + CalibrationSpeechSeconds),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
        }
        finally
        {
            try
            {
                capture.StopRecording();
            }
            catch
            {
                // Thiết bị có thể đã tự dừng.
            }
        }

        Exception? stopError = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(4));
        ClearActiveCapture();

        if (wasCancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (stopError is not null)
        {
            throw new InvalidOperationException(stopError.Message, stopError);
        }
    }

    private async Task PlayPcmBytesAsync(
        string deviceId,
        byte[] pcmBytes,
        int speakerVolume,
        double previewBoostDb,
        CancellationToken cancellationToken)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var outputDevice = enumerator.GetDevice(deviceId);
        using var memory = new MemoryStream(pcmBytes, writable: false);
        using var rawSource = new RawSourceWaveStream(memory, NetworkWaveFormat);

#pragma warning disable CS0618
        using var output = new WasapiOut(
            outputDevice,
            AudioClientShareMode.Shared,
            true,
            80);
#pragma warning restore CS0618

        SetActiveOutput(output);
        try
        {
            var protectedOutput = new ProtectedOutputWaveProvider16(rawSource)
            {
                MaximumGainDb = 18.0,
                GainDb = GetSpeakerGainDb(speakerVolume) + previewBoostDb
            };
            output.Init(protectedOutput);
            await PlayAndWaitAsync(output, cancellationToken);
        }
        finally
        {
            ClearActiveOutput();
        }
    }

    private static async Task PlayAndWaitAsync(
        IWavePlayer output,
        CancellationToken cancellationToken)
    {
        var stopped = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        output.PlaybackStopped += (_, args) => stopped.TrySetResult(args.Exception);
        using var registration = cancellationToken.Register(output.Stop);

        output.Play();
        Exception? playbackError = await stopped.Task;
        cancellationToken.ThrowIfCancellationRequested();

        if (playbackError is not null)
        {
            throw new InvalidOperationException(playbackError.Message, playbackError);
        }
    }

    private static void AccumulatePcm16(
        byte[] buffer,
        ref double sumSquares,
        ref double peak,
        ref long sampleCount)
    {
        int end = buffer.Length - sizeof(short) + 1;
        for (int index = 0; index < end; index += sizeof(short))
        {
            short raw = (short)(buffer[index] | buffer[index + 1] << 8);
            double sample = raw / 32768.0;
            sumSquares += sample * sample;
            peak = Math.Max(peak, Math.Abs(sample));
            sampleCount++;
        }
    }

    private static (double RmsDbfs, double PeakDbfs) CalculatePcm16Levels(
        byte[] buffer,
        int offset,
        int count)
    {
        double sumSquares = 0;
        double peak = 0;
        int sampleCount = 0;
        int end = Math.Min(buffer.Length, offset + count) - sizeof(short) + 1;

        for (int index = offset; index < end; index += sizeof(short))
        {
            short raw = (short)(buffer[index] | buffer[index + 1] << 8);
            double sample = raw / 32768.0;
            sumSquares += sample * sample;
            peak = Math.Max(peak, Math.Abs(sample));
            sampleCount++;
        }

        if (sampleCount == 0)
        {
            return (-96, -96);
        }

        return (
            LinearToDb(Math.Sqrt(sumSquares / sampleCount)),
            LinearToDb(peak));
    }

    private static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return -96;
        }

        double[] ordered = values.OrderBy(value => value).ToArray();
        double position = Math.Clamp(percentile, 0, 1) * (ordered.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return ordered[lower];
        }

        double fraction = position - lower;
        return ordered[lower] + (ordered[upper] - ordered[lower]) * fraction;
    }

    private static double DbfsToPercent(double dbfs)
    {
        return Math.Clamp((dbfs + 60.0) / 60.0 * 100.0, 0.0, 100.0);
    }

    private static double LinearToDb(double value)
    {
        return value <= 0.0000158489 ? -96.0 : 20.0 * Math.Log10(value);
    }

    private static double GetSpeakerGainDb(int speakerVolumePercent)
    {
        int clamped = Math.Clamp(speakerVolumePercent, 0, 120);
        if (clamped <= 0)
        {
            return -60;
        }

        if (clamped <= 100)
        {
            return 20.0 * Math.Log10(clamped / 100.0);
        }

        return (clamped - 100) / 20.0 * 3.0;
    }

    private static string FormatSignedDb(double value)
    {
        return $"{(value >= 0 ? "+" : string.Empty)}{value:F1} dB";
    }

    private static void EnsureCapacity(ref byte[] buffer, int requiredLength)
    {
        if (buffer.Length >= requiredLength)
        {
            return;
        }

        Array.Resize(ref buffer, Math.Max(requiredLength, buffer.Length * 2));
    }

    private void SetActiveCapture(WasapiCapture capture)
    {
        lock (_syncRoot)
        {
            _activeCapture = capture;
        }
    }

    private void ClearActiveCapture()
    {
        lock (_syncRoot)
        {
            _activeCapture = null;
        }
    }

    private void SetActiveOutput(WasapiOut output)
    {
        lock (_syncRoot)
        {
            _activeOutput = output;
        }
    }

    private void ClearActiveOutput()
    {
        lock (_syncRoot)
        {
            _activeOutput = null;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // File tạm sẽ được Windows dọn sau nếu đang bị khóa ngoài ý muốn.
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed record CapturedMicrophoneAudio(
        byte[] PcmBytes,
        double PeakDbfs,
        double RmsDbfs);

    private sealed class RoomTalkChimeSampleProvider : ISampleProvider
    {
        private const int ChimeSampleRate = 48000;
        private const double FirstToneSeconds = 0.42;
        private const double PauseSeconds = 0.08;
        private const double SecondToneSeconds = 0.52;
        private const double FadeSeconds = 0.018;

        private readonly int _totalSamples;
        private int _position;

        public RoomTalkChimeSampleProvider()
        {
            _totalSamples = (int)((FirstToneSeconds + PauseSeconds + SecondToneSeconds) * ChimeSampleRate);
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(ChimeSampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int available = _totalSamples - _position;
            int samplesToWrite = Math.Min(count, Math.Max(available, 0));

            for (int index = 0; index < samplesToWrite; index++)
            {
                int absoluteSample = _position + index;
                double time = absoluteSample / (double)ChimeSampleRate;
                buffer[offset + index] = CreateSample(time);
            }

            _position += samplesToWrite;
            return samplesToWrite;
        }

        private static float CreateSample(double time)
        {
            if (time < FirstToneSeconds)
            {
                return CreateTone(time, FirstToneSeconds, 660.0);
            }

            double secondStart = FirstToneSeconds + PauseSeconds;
            if (time < secondStart)
            {
                return 0;
            }

            return CreateTone(time - secondStart, SecondToneSeconds, 880.0);
        }

        private static float CreateTone(double localTime, double duration, double frequency)
        {
            double fadeIn = Math.Min(1.0, localTime / FadeSeconds);
            double fadeOut = Math.Min(1.0, Math.Max(0.0, duration - localTime) / FadeSeconds);
            double envelope = Math.Min(fadeIn, fadeOut);
            return (float)(Math.Sin(2.0 * Math.PI * frequency * localTime) * 0.35 * envelope);
        }
    }
}
