namespace RoomTalk.Models;

public sealed record AudioCalibrationResult(
    bool IsSuccess,
    string Message,
    double InputTrimDb = 0,
    double NoiseFloorDbfs = -60,
    double SpeechLevelDbfs = -30,
    double PeakDbfs = -12,
    double SuggestedMaximumAgcGainDb = 18)
{
    public static AudioCalibrationResult Failure(string message) =>
        new(false, message);
}
