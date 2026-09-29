using RoomTalk.Models;

namespace RoomTalk.Services;

public interface INetworkSpeakerService
{
    Task<IReadOnlyList<NetworkSpeakerItem>> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        IEnumerable<NetworkSpeakerItem> speakers,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NetworkSpeakerItem>> ScanAsync(
        string startIp,
        string endIp,
        IProgress<NetworkScanProgress>? progress,
        CancellationToken cancellationToken = default);

    Task<NetworkSpeakerItem> ProbeAsync(
        string ipAddress,
        CancellationToken cancellationToken = default);
}
