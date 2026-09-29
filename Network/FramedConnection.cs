using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;

namespace RoomTalk.Network;

internal sealed class FramedConnection : IAsyncDisposable
{
    private const int HeaderLength = 5;
    private const int MaximumPayloadLength = 2 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _disposed;

    public FramedConnection(NetworkStream stream)
    {
        _stream = stream;
    }

    public Task SendControlAsync(ControlMessage message, CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        return SendFrameAsync(WireFrameType.Control, payload, cancellationToken);
    }

    public Task SendAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken)
    {
        return SendFrameAsync(WireFrameType.Audio, audioData, cancellationToken);
    }

    public async Task<(WireFrameType Type, byte[] Payload)?> ReadFrameAsync(
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[HeaderLength];
        bool hasHeader = await ReadExactlyOrEndAsync(header, cancellationToken);
        if (!hasHeader)
        {
            return null;
        }

        WireFrameType type = (WireFrameType)header[0];
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1, 4));
        if (payloadLength < 0 || payloadLength > MaximumPayloadLength)
        {
            throw new InvalidDataException($"Kích thước gói RoomTalk không hợp lệ: {payloadLength} byte.");
        }

        byte[] payload = new byte[payloadLength];
        if (payloadLength > 0)
        {
            bool hasPayload = await ReadExactlyOrEndAsync(payload, cancellationToken);
            if (!hasPayload)
            {
                throw new EndOfStreamException("Kết nối bị đóng khi đang nhận gói RoomTalk.");
            }
        }

        return (type, payload);
    }

    public static ControlMessage DeserializeControl(byte[] payload)
    {
        return JsonSerializer.Deserialize<ControlMessage>(payload, JsonOptions)
               ?? throw new InvalidDataException("Không đọc được thông điệp điều khiển RoomTalk.");
    }

    private async Task SendFrameAsync(
        WireFrameType type,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(FramedConnection));
        }

        byte[] header = new byte[HeaderLength];
        header[0] = (byte)type;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1, 4), payload.Length);

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _stream.WriteAsync(header, cancellationToken);
            if (!payload.IsEmpty)
            {
                await _stream.WriteAsync(payload, cancellationToken);
            }

            await _stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<bool> ReadExactlyOrEndAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await _stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
            {
                if (offset == 0)
                {
                    return false;
                }

                throw new EndOfStreamException("Kết nối bị đóng giữa một gói RoomTalk.");
            }

            offset += read;
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sendLock.Dispose();
        await _stream.DisposeAsync();
    }
}
