using NAudio.Wave;

namespace RoomTalk.Services;

/// <summary>
/// Điều chỉnh mức phát theo dB và dùng limiter tuyến tính theo block để bảo vệ đầu ra.
/// Không dùng soft clipping nên giữ độ trong tốt hơn khi cần tăng nhẹ âm lượng.
/// </summary>
internal sealed class ProtectedOutputWaveProvider16 : IWaveProvider
{
    private const double LimiterCeiling = 0.891250938; // -1 dBFS

    private readonly IWaveProvider _source;
    private double _gainDb;
    private double _maximumGainDb = 3.0;
    private double _limiterGain = 1.0;

    public ProtectedOutputWaveProvider16(IWaveProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));

        if (source.WaveFormat.BitsPerSample != 16)
        {
            throw new ArgumentException("Nguồn phát phải là PCM 16-bit.", nameof(source));
        }
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>
    /// Giới hạn mặc định của luồng giao tiếp là +3 dB. Bài nghe thử microphone
    /// có thể tạm nâng giới hạn để người dùng chắc chắn nghe được bản ghi.
    /// </summary>
    public double MaximumGainDb
    {
        get => _maximumGainDb;
        set
        {
            _maximumGainDb = Math.Clamp(value, 0.0, 18.0);
            _gainDb = Math.Clamp(_gainDb, -60.0, _maximumGainDb);
        }
    }

    public double GainDb
    {
        get => _gainDb;
        set => _gainDb = Math.Clamp(value, -60.0, MaximumGainDb);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        int bytesRead = _source.Read(buffer, offset, count);
        if (bytesRead <= 0)
        {
            return bytesRead;
        }

        double requestedGain = Math.Pow(10.0, GainDb / 20.0);
        double peakAfterGain = 0;
        int end = offset + bytesRead - sizeof(short) + 1;

        for (int index = offset; index < end; index += sizeof(short))
        {
            short raw = (short)(buffer[index] | buffer[index + 1] << 8);
            double sample = raw / 32768.0;
            peakAfterGain = Math.Max(peakAfterGain, Math.Abs(sample * requestedGain));
        }

        double targetLimiterGain = peakAfterGain > LimiterCeiling
            ? LimiterCeiling / peakAfterGain
            : 1.0;

        if (targetLimiterGain < _limiterGain)
        {
            _limiterGain = targetLimiterGain;
        }
        else
        {
            _limiterGain += (1.0 - _limiterGain) * 0.10;
        }

        double totalGain = requestedGain * _limiterGain;
        for (int index = offset; index < end; index += sizeof(short))
        {
            short raw = (short)(buffer[index] | buffer[index + 1] << 8);
            double sample = Math.Clamp(raw / 32768.0 * totalGain, -0.999969, 0.999969);

            short output = (short)Math.Clamp(
                (int)Math.Round(sample * short.MaxValue),
                short.MinValue,
                short.MaxValue);

            buffer[index] = (byte)(output & 0xFF);
            buffer[index + 1] = (byte)((output >> 8) & 0xFF);
        }

        return bytesRead;
    }
}
