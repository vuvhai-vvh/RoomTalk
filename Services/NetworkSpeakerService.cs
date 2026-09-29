using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Xml.Linq;
using RoomTalk.Models;
using System.IO;

namespace RoomTalk.Services;

public sealed class NetworkSpeakerService : INetworkSpeakerService
{
    private static readonly int[] CandidatePorts = [80, 443, 554, 8000];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _configurationPath;

    public NetworkSpeakerService()
    {
        string dataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RoomTalk");

        Directory.CreateDirectory(dataFolder);
        _configurationPath = Path.Combine(dataFolder, "network-speakers.json");
    }

    public async Task<IReadOnlyList<NetworkSpeakerItem>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_configurationPath))
        {
            return [];
        }

        await using FileStream stream = File.OpenRead(_configurationPath);
        List<NetworkSpeakerConfigRecord>? records =
            await JsonSerializer.DeserializeAsync<List<NetworkSpeakerConfigRecord>>(
                stream,
                JsonOptions,
                cancellationToken);

        return records?.Select(record => record.ToItem()).ToList() ?? [];
    }

    public async Task SaveAsync(
        IEnumerable<NetworkSpeakerItem> speakers,
        CancellationToken cancellationToken = default)
    {
        List<NetworkSpeakerConfigRecord> records = speakers
            .OrderBy(item => item.IpAddress, StringComparer.OrdinalIgnoreCase)
            .Select(NetworkSpeakerConfigRecord.FromItem)
            .ToList();

        string temporaryPath = _configurationPath + ".tmp";

        await using (FileStream stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                records,
                JsonOptions,
                cancellationToken);
        }

        File.Move(temporaryPath, _configurationPath, overwrite: true);
    }

    public async Task<IReadOnlyList<NetworkSpeakerItem>> ScanAsync(
        string startIp,
        string endIp,
        IProgress<NetworkScanProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<IPAddress> addresses = BuildAddressRange(startIp, endIp);
        var found = new List<NetworkSpeakerItem>();
        var resultLock = new object();
        int completed = 0;
        int foundCount = 0;

        using var throttler = new SemaphoreSlim(24, 24);

        IEnumerable<Task> tasks = addresses.Select(async address =>
        {
            await throttler.WaitAsync(cancellationToken);

            try
            {
                NetworkSpeakerItem speaker = await ProbeAsync(
                    address.ToString(),
                    cancellationToken);

                if (speaker.IsReachable && speaker.IsHikvisionCandidate)
                {
                    lock (resultLock)
                    {
                        found.Add(speaker);
                        foundCount++;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Một IP không phản hồi không được coi là lỗi của toàn bộ quá trình quét.
            }
            finally
            {
                int currentCompleted = Interlocked.Increment(ref completed);
                progress?.Report(new NetworkScanProgress(
                    currentCompleted,
                    addresses.Count,
                    address.ToString(),
                    Volatile.Read(ref foundCount)));

                throttler.Release();
            }
        });

        await Task.WhenAll(tasks);

        return found
            .OrderBy(item => ParseAddressValue(item.IpAddress))
            .ToList();
    }

    public async Task<NetworkSpeakerItem> ProbeAsync(
        string ipAddress,
        CancellationToken cancellationToken = default)
    {
        if (!IPAddress.TryParse(ipAddress, out IPAddress? address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Địa chỉ IPv4 không hợp lệ.", nameof(ipAddress));
        }

        var openPorts = new List<int>();

        foreach (int port in CandidatePorts)
        {
            if (await IsTcpPortOpenAsync(address, port, cancellationToken))
            {
                openPorts.Add(port);
            }
        }

        var item = new NetworkSpeakerItem
        {
            Name = $"Thiết bị IP {address.GetAddressBytes()[3]}",
            IpAddress = ipAddress,
            ManagementPort = openPorts.Contains(80)
                ? 80
                : openPorts.Contains(443)
                    ? 443
                    : openPorts.FirstOrDefault(80),
            Protocol = BuildProtocolSummary(openPorts),
            LastSeen = DateTimeOffset.Now
        };

        if (openPorts.Count == 0)
        {
            item.Status = NetworkSpeakerStatus.Offline;
            item.LastSeen = null;
            item.Note = "Không tìm thấy cổng dịch vụ thông dụng.";
            return item;
        }

        DeviceIdentity identity = await TryReadDeviceIdentityAsync(
            address,
            openPorts,
            cancellationToken);

        item.Manufacturer = identity.Manufacturer;
        item.Model = identity.Model;
        item.Status = identity.AuthenticationRequired
            ? NetworkSpeakerStatus.AuthenticationRequired
            : NetworkSpeakerStatus.Online;
        item.Note = identity.Note;

        if (identity.IsIsapiEndpoint &&
            !item.Protocol.Contains("ISAPI", StringComparison.OrdinalIgnoreCase))
        {
            item.Protocol = string.IsNullOrWhiteSpace(item.Protocol)
                ? "ISAPI"
                : item.Protocol + ", ISAPI";
        }

        if (identity.IsHikvision &&
            item.Name.StartsWith("Thiết bị IP", StringComparison.Ordinal))
        {
            item.Name = $"Loa Hikvision {address.GetAddressBytes()[3]}";
        }

        return item;
    }

    private static async Task<bool> IsTcpPortOpenAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient(address.AddressFamily);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(450));

        try
        {
            await client.ConnectAsync(address, port, timeout.Token);
            return client.Connected;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static async Task<DeviceIdentity> TryReadDeviceIdentityAsync(
        IPAddress address,
        IReadOnlyCollection<int> openPorts,
        CancellationToken cancellationToken)
    {
        bool hasHttp = openPorts.Contains(80);
        bool hasHttps = openPorts.Contains(443);

        if (!hasHttp && !hasHttps)
        {
            bool hikvisionPort = openPorts.Contains(8000);
            return new DeviceIdentity(
                hikvisionPort ? "Ứng viên Hikvision" : "Chưa xác định",
                "Chưa xác định",
                false,
                false,
                hikvisionPort,
                hikvisionPort
                    ? "Cổng 8000 đang mở. Cần model và tài khoản để xác nhận thiết bị Hikvision."
                    : "Thiết bị có dịch vụ mạng nhưng chưa đọc được thông tin nhận dạng.");
        }

        string scheme = hasHttp ? "http" : "https";
        int port = hasHttp ? 80 : 443;
        Uri uri = new($"{scheme}://{address}:{port}/ISAPI/System/deviceInfo");

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            AllowAutoRedirect = false
        };

        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMilliseconds(1200)
        };

        try
        {
            using HttpResponseMessage response = await client.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            string serverHeader = response.Headers.Server.ToString();
            bool serverSaysHikvision =
                serverHeader.Contains("Hikvision", StringComparison.OrdinalIgnoreCase);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new DeviceIdentity(
                    serverSaysHikvision ? "Hikvision" : "Ứng viên Hikvision/ISAPI",
                    "Cần đăng nhập để đọc model",
                    true,
                    true,
                    true,
                    "Thiết bị phản hồi ISAPI nhưng yêu cầu tài khoản thiết bị.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new DeviceIdentity(
                    serverSaysHikvision ? "Hikvision" : "Chưa xác định",
                    "Chưa xác định",
                    false,
                    false,
                    serverSaysHikvision,
                    $"HTTP {(int)response.StatusCode}; chưa đọc được thông tin thiết bị.");
            }

            string content = await response.Content.ReadAsStringAsync(cancellationToken);
            XDocument document = XDocument.Parse(content);

            string manufacturer = FindElementValue(document, "manufacturer");
            string model = FindElementValue(document, "model");
            string deviceName = FindElementValue(document, "deviceName");

            bool isHikvision =
                serverSaysHikvision ||
                manufacturer.Contains("Hikvision", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("Hikvision", StringComparison.OrdinalIgnoreCase);

            return new DeviceIdentity(
                string.IsNullOrWhiteSpace(manufacturer)
                    ? isHikvision ? "Hikvision" : "Chưa xác định"
                    : manufacturer,
                string.IsNullOrWhiteSpace(model) ? "Chưa xác định" : model,
                false,
                true,
                isHikvision,
                string.IsNullOrWhiteSpace(deviceName)
                    ? "Đã đọc được ISAPI deviceInfo."
                    : $"Tên thiết bị: {deviceName}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DeviceIdentity(
                "Chưa xác định",
                "Chưa xác định",
                false,
                false,
                false,
                "Thiết bị có cổng mạng mở nhưng truy vấn nhận dạng đã hết thời gian.");
        }
        catch (HttpRequestException)
        {
            return new DeviceIdentity(
                "Chưa xác định",
                "Chưa xác định",
                false,
                false,
                false,
                "Không đọc được thông tin HTTP/ISAPI.");
        }
        catch (Exception exception) when (
            exception is System.Xml.XmlException or InvalidOperationException)
        {
            return new DeviceIdentity(
                "Chưa xác định",
                "Chưa xác định",
                false,
                false,
                false,
                "Phản hồi nhận dạng không đúng định dạng ISAPI.");
        }
    }

    private static string FindElementValue(XDocument document, string localName)
    {
        return document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))
            ?.Value
            ?.Trim() ?? string.Empty;
    }

    private static string BuildProtocolSummary(IEnumerable<int> ports)
    {
        var protocols = new List<string>();

        foreach (int port in ports.Order())
        {
            protocols.Add(port switch
            {
                80 => "HTTP:80",
                443 => "HTTPS:443",
                554 => "RTSP:554",
                8000 => "Hikvision SDK:8000",
                _ => $"TCP:{port}"
            });
        }

        return string.Join(", ", protocols);
    }

    private static IReadOnlyList<IPAddress> BuildAddressRange(
        string startIp,
        string endIp)
    {
        if (!IPAddress.TryParse(startIp, out IPAddress? start) ||
            !IPAddress.TryParse(endIp, out IPAddress? end) ||
            start.AddressFamily != AddressFamily.InterNetwork ||
            end.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Dải quét phải là địa chỉ IPv4 hợp lệ.");
        }

        uint startValue = ParseAddressValue(start);
        uint endValue = ParseAddressValue(end);

        if (startValue > endValue)
        {
            throw new ArgumentException("IP bắt đầu phải nhỏ hơn hoặc bằng IP kết thúc.");
        }

        const uint maximumAddressCount = 512;
        uint count = endValue - startValue + 1;

        if (count > maximumAddressCount)
        {
            throw new ArgumentException(
                $"Bản thử nghiệm chỉ quét tối đa {maximumAddressCount} địa chỉ mỗi lần.");
        }

        var addresses = new List<IPAddress>((int)count);

        for (uint value = startValue; value <= endValue; value++)
        {
            addresses.Add(ToAddress(value));

            if (value == uint.MaxValue)
            {
                break;
            }
        }

        return addresses;
    }

    private static uint ParseAddressValue(string ipAddress)
    {
        return IPAddress.TryParse(ipAddress, out IPAddress? address)
            ? ParseAddressValue(address)
            : uint.MaxValue;
    }

    private static uint ParseAddressValue(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) |
               ((uint)bytes[1] << 16) |
               ((uint)bytes[2] << 8) |
               bytes[3];
    }

    private static IPAddress ToAddress(uint value)
    {
        return new IPAddress([
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value
        ]);
    }

    private sealed record DeviceIdentity(
        string Manufacturer,
        string Model,
        bool AuthenticationRequired,
        bool IsIsapiEndpoint,
        bool IsHikvision,
        string Note);
}
