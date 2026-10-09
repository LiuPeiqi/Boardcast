using System.Buffers.Binary;
using System.Text.Json;
namespace Boardcast.Core;
public static class WireProtocol
{
    public const int MaximumFrameBytes = 4 * 1024 * 1024;
    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, BoardcastJson.Options);
        if (body.Length > MaximumFrameBytes) throw new InvalidDataException("消息超过协议限额。");
        var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, body.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is <= 0 or > MaximumFrameBytes) throw new InvalidDataException("消息长度无效。");
        var body = new byte[length]; await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(body, BoardcastJson.Options) ?? throw new InvalidDataException("空协议消息。");
    }
}
