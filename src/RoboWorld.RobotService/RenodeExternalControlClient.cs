using System.Buffers.Binary;
using System.Net.Sockets;

namespace RoboWorld.RobotService;

/// <summary>Renode 1.17 external-control client for exact virtual-time advancement.</summary>
internal sealed class RenodeExternalControlClient : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private ushort _requestId;

    private RenodeExternalControlClient(TcpClient client) { _client = client; _client.NoDelay = true; _stream = client.GetStream(); }

    public static async Task<RenodeExternalControlClient> ConnectAsync(int port, CancellationToken token)
    {
        var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port, token);
        var result = new RenodeExternalControlClient(client);
        try
        {
            var version = new byte[4];
            await result.RequestAsync(0, version, token);
            if (BinaryPrimitives.ReadUInt32LittleEndian(version) != 0) throw new InvalidDataException("Unsupported Renode external-control protocol version.");
            return result;
        }
        catch { await result.DisposeAsync(); throw; }
    }

    public Task RunForAsync(long deltaNs, CancellationToken token)
    {
        if (deltaNs <= 0) throw new ArgumentOutOfRangeException(nameof(deltaNs));
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, (ulong)deltaNs);
        return RequestAsync(1, payload, token);
    }

    public async Task<long> GetTimeNsAsync(CancellationToken token)
    {
        var payload = new byte[8];
        await RequestAsync(2, payload, token);
        var nanoseconds = BinaryPrimitives.ReadUInt64LittleEndian(payload);
        return nanoseconds > long.MaxValue ? throw new InvalidDataException("Renode virtual time exceeded signed nanosecond range.") : (long)nanoseconds;
    }

    private async Task RequestAsync(ushort command, byte[] payload, CancellationToken token)
    {
        var id = (ushort)(0x8000 | _requestId++);
        var message = new byte[payload.Length + 3];
        BinaryPrimitives.WriteUInt16LittleEndian(message, command);
        message[2] = 0;
        payload.CopyTo(message, 3);
        var header = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(header, id);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(2), (uint)message.Length);
        await _stream.WriteAsync(header, token);
        await _stream.WriteAsync(message, token);
        var responseHeader = new byte[6];
        await ReadExactlyAsync(responseHeader, token);
        var responseId = BinaryPrimitives.ReadUInt16LittleEndian(responseHeader);
        var responseSize = BinaryPrimitives.ReadUInt32LittleEndian(responseHeader.AsSpan(2));
        if (responseId != id || responseSize is < 3 or > 1024) throw new InvalidDataException("Malformed Renode external-control response header.");
        var response = new byte[checked((int)responseSize)];
        await ReadExactlyAsync(response, token);
        if (BinaryPrimitives.ReadUInt16LittleEndian(response) != command || response[2] != 2) throw new IOException($"Renode external-control command {command} failed (type={response[2]}).");
        if (response.Length != payload.Length + 3 && response.Length != 3) throw new InvalidDataException("Unexpected Renode external-control response size.");
        if (response.Length > 3) response.AsSpan(3).CopyTo(payload);
    }

    private async Task ReadExactlyAsync(Memory<byte> buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer[offset..], token);
            if (read == 0) throw new EndOfStreamException("Renode external-control socket closed.");
            offset += read;
        }
    }

    public async ValueTask DisposeAsync() { await _stream.DisposeAsync(); _client.Dispose(); }
}
