using RoomTalk.Models;

namespace RoomTalk.Services;

/// <summary>
/// RoomTalk Voice Engine V7 - ưu tiên độ rõ của lời nói.
///
/// Pipeline mặc định chỉ gồm các tầng cần thiết:
/// high-pass 80 Hz -> cân mức giọng chậm, ổn định -> giảm nền nhẹ khi im lặng -> limiter bảo vệ đỉnh.
/// Không dùng phép trừ echo tự chế, hard noise gate hoặc compressor nối tầng vì các khối đó
/// có thể làm mất phụ âm, làm giọng mỏng và khó nghe rõ chữ.
/// </summary>
internal sealed class RoomTalkVoiceProcessor
{
    private const float Int16Scale = 32768f;
    private const double SilenceDbfs = -96.0;
    private const double LimiterCeilingDbfs = -1.0;

    private readonly int _subframeSamples;
    private readonly BiquadHighPass _highPass;

    private string? _activeDeviceId;
    private string? _activeProfileSignature;
    private double _noiseFloorDbfs = -60.0;
    private double _adaptiveGainDb;
    private double _appliedDynamicGainDb;
    private double _silenceAttenuationDb;
    private double _limiterGain = 1.0;
    private double _currentStaticInputGainDb;
    private int _voiceHangoverFrames;
    private bool _voiceActive;

    public RoomTalkVoiceProcessor(int sampleRate)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        _subframeSamples = Math.Max(1, sampleRate / 100); // 10 ms
        _highPass = new BiquadHighPass(sampleRate, 80.0, 0.70710678);
    }

    public VoiceProcessingMetrics LastMetrics { get; private set; } = VoiceProcessingMetrics.Empty;

    /// <summary>
    /// Bắt đầu một lượt nói. Gain đã học được giữ lại nếu vẫn dùng cùng microphone
    /// và cùng cấu hình, nhờ đó đầu câu không bị nhỏ rồi mới lớn dần.
    /// </summary>
    public void BeginStream(string? inputDeviceId, AudioSettingsData settings)
    {
        string profileSignature = string.Join(
            "|",
            inputDeviceId ?? string.Empty,
            settings.InputTrimDb.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            settings.TargetSpeechLevelDbfs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            settings.MaximumAgcGainDb.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            settings.MaximumCombinedInputGainDb.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            settings.LastCalibrationUtc.HasValue
                ? settings.LastCalibrationUtc.Value.UtcDateTime.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty);

        bool deviceChanged = !string.Equals(
            _activeDeviceId,
            inputDeviceId,
            StringComparison.OrdinalIgnoreCase);
        bool profileChanged = !string.Equals(
            _activeProfileSignature,
            profileSignature,
            StringComparison.Ordinal);

        _activeDeviceId = inputDeviceId;
        _activeProfileSignature = profileSignature;
        _highPass.Reset();
        _silenceAttenuationDb = 0;
        _limiterGain = 1;
        _currentStaticInputGainDb = 0;
        _voiceHangoverFrames = 0;
        _voiceActive = false;

        if (deviceChanged || profileChanged)
        {
            bool calibrationValid = settings.IsCalibrationValidForCurrentInput;
            double safeTrimDb = calibrationValid
                ? Math.Clamp(settings.InputTrimDb, -6.0, 6.0)
                : 0.0;

            _noiseFloorDbfs = calibrationValid
                ? Math.Clamp(settings.CalibratedNoiseFloorDbfs + safeTrimDb, -90.0, -30.0)
                : -60.0;

            double speechAfterTrimDbfs = calibrationValid
                ? settings.CalibratedSpeechLevelDbfs + safeTrimDb
                : -22.0;

            double maximumAdaptiveGainDb = Math.Min(
                Math.Clamp(settings.MaximumAgcGainDb, 0.0, 10.0),
                Math.Max(0.0, settings.MaximumCombinedInputGainDb - Math.Max(0.0, safeTrimDb)));

            _adaptiveGainDb = settings.AutomaticGainControl
                ? Math.Clamp(
                    settings.TargetSpeechLevelDbfs - speechAfterTrimDbfs,
                    -6.0,
                    maximumAdaptiveGainDb)
                : 0.0;
            _appliedDynamicGainDb = _adaptiveGainDb;
        }

        LastMetrics = VoiceProcessingMetrics.Empty;
    }

    public VoiceProcessingMetrics Process(byte[] pcm16Frame, AudioSettingsData settings)
    {
        ArgumentNullException.ThrowIfNull(pcm16Frame);
        ArgumentNullException.ThrowIfNull(settings);

        int sampleCount = pcm16Frame.Length / sizeof(short);
        if (sampleCount <= 0)
        {
            return LastMetrics;
        }

        float[] samples = new float[sampleCount];

        double manualGain = Math.Clamp(settings.MicrophoneVolume / 100.0, 0.0, 1.0);
        double manualGainDb = manualGain > 0 ? LinearToDb(manualGain) : -60.0;
        double calibrationTrimDb = settings.IsCalibrationValidForCurrentInput
            ? Math.Clamp(settings.InputTrimDb, -6.0, 6.0)
            : 0.0;

        _currentStaticInputGainDb = Math.Min(
            calibrationTrimDb + manualGainDb,
            Math.Clamp(settings.MaximumCombinedInputGainDb, 6.0, 14.0));
        double staticInputGain = DbToLinear(_currentStaticInputGainDb);

        double inputSumSquares = 0;
        for (int sampleIndex = 0, byteIndex = 0;
             sampleIndex < sampleCount;
             sampleIndex++, byteIndex += sizeof(short))
        {
            short raw = (short)(pcm16Frame[byteIndex] | pcm16Frame[byteIndex + 1] << 8);
            double sample = raw / (double)Int16Scale;

            if (settings.HighPassFilter)
            {
                sample = _highPass.Process(sample);
            }

            sample *= staticInputGain;
            sample = Math.Clamp(sample, -2.0, 2.0);
            samples[sampleIndex] = (float)sample;
            inputSumSquares += sample * sample;
        }

        double inputRmsDbfs = LinearToDb(Math.Sqrt(inputSumSquares / sampleCount));
        double outputPeakBeforeLimiter = 0;

        for (int start = 0; start < sampleCount; start += _subframeSamples)
        {
            int count = Math.Min(_subframeSamples, sampleCount - start);
            ProcessSubframe(samples.AsSpan(start, count), settings);

            for (int index = start; index < start + count; index++)
            {
                outputPeakBeforeLimiter = Math.Max(outputPeakBeforeLimiter, Math.Abs(samples[index]));
            }
        }

        double limiterReductionDb = 0;
        if (settings.PeakLimiterEnabled)
        {
            double ceiling = DbToLinear(LimiterCeilingDbfs);
            double targetLimiterGain = outputPeakBeforeLimiter > ceiling
                ? ceiling / outputPeakBeforeLimiter
                : 1.0;

            if (targetLimiterGain < _limiterGain)
            {
                _limiterGain = targetLimiterGain; // attack ngay để không clipping
            }
            else
            {
                _limiterGain += (1.0 - _limiterGain) * 0.08; // release mềm
            }

            limiterReductionDb = Math.Min(0.0, LinearToDb(_limiterGain));
        }
        else
        {
            _limiterGain = 1.0;
        }

        double outputSumSquares = 0;
        double outputPeak = 0;
        for (int sampleIndex = 0, byteIndex = 0;
             sampleIndex < sampleCount;
             sampleIndex++, byteIndex += sizeof(short))
        {
            double sample = Math.Clamp(samples[sampleIndex] * _limiterGain, -0.999969, 0.999969);
            outputSumSquares += sample * sample;
            outputPeak = Math.Max(outputPeak, Math.Abs(sample));

            short output = (short)Math.Clamp(
                (int)Math.Round(sample * short.MaxValue),
                short.MinValue,
                short.MaxValue);

            pcm16Frame[byteIndex] = (byte)(output & 0xFF);
            pcm16Frame[byteIndex + 1] = (byte)((output >> 8) & 0xFF);
        }

        LastMetrics = new VoiceProcessingMetrics(
            inputRmsDbfs,
            LinearToDb(Math.Sqrt(outputSumSquares / sampleCount)),
            LinearToDb(outputPeak),
            _noiseFloorDbfs,
            _adaptiveGainDb,
            limiterReductionDb,
            0,
            0,
            false,
            _voiceActive);

        return LastMetrics;
    }

    private void ProcessSubframe(Span<float> samples, AudioSettingsData settings)
    {
        double sumSquares = 0;
        double peak = 0;
        for (int index = 0; index < samples.Length; index++)
        {
            double sample = samples[index];
            sumSquares += sample * sample;
            peak = Math.Max(peak, Math.Abs(sample));
        }

        double rmsDbfs = LinearToDb(Math.Sqrt(sumSquares / samples.Length));
        double peakDbfs = LinearToDb(peak);
        double speechThresholdDbfs = Math.Max(_noiseFloorDbfs + 7.0, -55.0);
        bool instantaneousSpeech =
            rmsDbfs > speechThresholdDbfs &&
            peakDbfs > -48.0;

        if (instantaneousSpeech)
        {
            _voiceHangoverFrames = 30; // 300 ms để không cắt âm cuối và phụ âm nhẹ
        }
        else if (_voiceHangoverFrames > 0)
        {
            _voiceHangoverFrames--;
        }

        _voiceActive = instantaneousSpeech || _voiceHangoverFrames > 0;
        UpdateNoiseFloor(rmsDbfs, instantaneousSpeech);

        double targetSilenceAttenuationDb = 0;
        if (settings.NoiseSuppression && !_voiceActive)
        {
            double maximumAttenuationDb = settings.NoiseSuppressionLevel switch
            {
                AudioNoiseSuppressionLevel.Low => -2.0,
                AudioNoiseSuppressionLevel.Moderate => -3.5,
                AudioNoiseSuppressionLevel.High => -6.0,
                AudioNoiseSuppressionLevel.VeryHigh => -7.0,
                _ => -3.5
            };

            double belowThresholdDb = Math.Max(0.0, speechThresholdDbfs - rmsDbfs);
            double amount = Math.Clamp(belowThresholdDb / 20.0, 0.0, 1.0);
            targetSilenceAttenuationDb = maximumAttenuationDb * amount;
        }

        // Mở ngay khi có giọng; giảm nền chậm để không nuốt đầu câu.
        double silenceSmoothing = targetSilenceAttenuationDb > _silenceAttenuationDb
            ? 0.75
            : 0.05;
        _silenceAttenuationDb +=
            (targetSilenceAttenuationDb - _silenceAttenuationDb) * silenceSmoothing;

        double maximumAdaptiveGainDb = Math.Min(
            Math.Clamp(settings.MaximumAgcGainDb, 0.0, 10.0),
            Math.Max(
                0.0,
                Math.Clamp(settings.MaximumCombinedInputGainDb, 6.0, 14.0) -
                Math.Max(0.0, _currentStaticInputGainDb)));

        if (settings.AutomaticGainControl && _voiceActive)
        {
            double desiredGainDb = settings.TargetSpeechLevelDbfs - rmsDbfs;
            desiredGainDb = Math.Clamp(desiredGainDb, -6.0, maximumAdaptiveGainDb);

            double noiseLimitedGainDb = settings.MaximumOutputNoiseLevelDbfs - _noiseFloorDbfs;
            desiredGainDb = Math.Min(desiredGainDb, Math.Max(0.0, noiseLimitedGainDb));

            // Hạ gain nhanh khi người nói lớn, nâng gain từ từ để không bơm tiếng nền.
            double gainSmoothing = desiredGainDb < _adaptiveGainDb ? 0.32 : 0.045;
            _adaptiveGainDb += (desiredGainDb - _adaptiveGainDb) * gainSmoothing;
        }
        else if (!settings.AutomaticGainControl)
        {
            _adaptiveGainDb += (0.0 - _adaptiveGainDb) * 0.20;
        }

        double targetDynamicGainDb = _adaptiveGainDb + _silenceAttenuationDb;
        double startGain = DbToLinear(_appliedDynamicGainDb);
        double endGain = DbToLinear(targetDynamicGainDb);
        double gainStep = samples.Length > 1
            ? (endGain - startGain) / (samples.Length - 1)
            : 0.0;

        double gain = startGain;
        for (int index = 0; index < samples.Length; index++)
        {
            samples[index] = (float)(samples[index] * gain);
            gain += gainStep;
        }

        _appliedDynamicGainDb = targetDynamicGainDb;
    }

    private void UpdateNoiseFloor(double rmsDbfs, bool instantaneousSpeech)
    {
        if (instantaneousSpeech || _voiceHangoverFrames > 0)
        {
            return;
        }

        double bounded = Math.Clamp(rmsDbfs, -90.0, -25.0);
        double smoothing = bounded < _noiseFloorDbfs ? 0.06 : 0.003;
        _noiseFloorDbfs += (bounded - _noiseFloorDbfs) * smoothing;
    }

    private static double DbToLinear(double db)
    {
        return Math.Pow(10.0, db / 20.0);
    }

    private static double LinearToDb(double value)
    {
        if (!double.IsFinite(value) || value <= 0.0000158489)
        {
            return SilenceDbfs;
        }

        return 20.0 * Math.Log10(value);
    }

    private sealed class BiquadHighPass
    {
        private readonly double _b0;
        private readonly double _b1;
        private readonly double _b2;
        private readonly double _a1;
        private readonly double _a2;

        private double _x1;
        private double _x2;
        private double _y1;
        private double _y2;

        public BiquadHighPass(int sampleRate, double cutoffHz, double q)
        {
            double omega = 2.0 * Math.PI * cutoffHz / sampleRate;
            double cosine = Math.Cos(omega);
            double sine = Math.Sin(omega);
            double alpha = sine / (2.0 * q);

            double b0 = (1.0 + cosine) / 2.0;
            double b1 = -(1.0 + cosine);
            double b2 = (1.0 + cosine) / 2.0;
            double a0 = 1.0 + alpha;
            double a1 = -2.0 * cosine;
            double a2 = 1.0 - alpha;

            _b0 = b0 / a0;
            _b1 = b1 / a0;
            _b2 = b2 / a0;
            _a1 = a1 / a0;
            _a2 = a2 / a0;
        }

        public double Process(double input)
        {
            double output =
                _b0 * input +
                _b1 * _x1 +
                _b2 * _x2 -
                _a1 * _y1 -
                _a2 * _y2;

            _x2 = _x1;
            _x1 = input;
            _y2 = _y1;
            _y1 = output;
            return output;
        }

        public void Reset()
        {
            _x1 = 0;
            _x2 = 0;
            _y1 = 0;
            _y2 = 0;
        }
    }
}
