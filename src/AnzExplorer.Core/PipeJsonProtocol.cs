using System.Buffers.Binary;
using System.Text.Json;

namespace AnzExplorer.Core;

public static class PipeJsonProtocol
{
    private const int HeaderLength = sizeof(int);
    private const int MaximumMessageLength = 1024 * 1024;

    public static async Task WriteAsync<T>(
        Stream stream,
        T message,
        JsonSerializerOptions options,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, options);
        if (payload.Length > MaximumMessageLength)
        {
            throw new InvalidDataException($"IPC message exceeds {MaximumMessageLength:N0} bytes.");
        }

        var header = new byte[HeaderLength];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(
        Stream stream,
        JsonSerializerOptions options,
        CancellationToken cancellationToken)
    {
        var header = new byte[HeaderLength];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (payloadLength is <= 0 or > MaximumMessageLength)
        {
            throw new InvalidDataException($"IPC message length {payloadLength:N0} is invalid.");
        }

        var payload = new byte[payloadLength];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload, options)
            ?? throw new InvalidDataException("IPC message contained an empty JSON value.");
    }

    /// <summary>
    /// For a request/response connection where the client sends one request and then only
    /// waits: cancels <paramref name="requestCancellation"/> if the client disconnects before
    /// <paramref name="stopWatching"/> is signalled. Any completed read (normally 0 bytes at
    /// end-of-stream) means the peer went away.
    /// </summary>
    public static async Task CancelOnDisconnectAsync(
        Stream stream,
        CancellationTokenSource requestCancellation,
        CancellationToken stopWatching)
    {
        var probe = new byte[1];
        try
        {
            await stream.ReadAsync(probe, stopWatching).ConfigureAwait(false);
            requestCancellation.Cancel();
        }
        catch (OperationCanceledException)
        {
            // The work finished first; stop watching.
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            requestCancellation.Cancel();
        }
    }
}

