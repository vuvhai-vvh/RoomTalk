namespace RoomTalk.Models;

/// <summary>
/// Thông số đo của một khung thoại sau khi đi qua RoomTalk Voice Engine.
/// Các mức âm dùng dBFS, trong đó 0 dBFS là mức tối đa của tín hiệu số.
/// </summary>
public sealed record VoiceProcessingMetrics(
    double InputRmsDbfs,
    double OutputRmsDbfs,
    double OutputPeakDbfs,
    double NoiseFloorDbfs,
    double AdaptiveGainDb,
    double LimiterReductionDb,
    double EchoCorrelation,
    double EchoReductionDb,
    bool EchoDetected,
    bool VoiceActive)
{
    public static VoiceProcessingMetrics Empty { get; } = new(
        -96,
        -96,
        -96,
        -60,
        0,
        0,
        0,
        0,
        false,
        false);
}
