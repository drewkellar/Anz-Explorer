using System.Diagnostics;
using System.IO.Pipes;
using AnzExplorer.Core;

namespace AnzExplorer.Core.Tests;

public sealed class IndexerClientTests
{
    [Fact]
    public async Task Unreachable_worker_backs_off_so_later_folders_fail_fast()
    {
        var client = new IndexerClient(
            pipeName: "AnzExplorer.Tests." + Guid.NewGuid().ToString("N"),
            connectTimeout: TimeSpan.FromMilliseconds(200),
            retryDelay: TimeSpan.FromMinutes(1));
        var folder = Path.GetTempPath();

        await Assert.ThrowsAsync<TimeoutException>(() => client.GetFolderSizeAsync(folder));
        Assert.True(client.IsTemporarilyUnavailable);

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<IOException>(() => client.GetFolderSizeAsync(folder));
        Assert.True(stopwatch.ElapsedMilliseconds < 150, $"Second request took {stopwatch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task Server_request_is_cancelled_when_client_disconnects()
    {
        var name = "AnzExplorer.Tests." + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(
            name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync(2_000));
        using var request = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();

        var watcher = PipeJsonProtocol.CancelOnDisconnectAsync(server, request, stop.Token);
        await client.DisposeAsync();
        await watcher.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(request.IsCancellationRequested);
    }

    [Fact]
    public async Task Finished_request_stops_watching_without_cancelling()
    {
        var name = "AnzExplorer.Tests." + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(
            name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync(2_000));
        using var request = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();

        var watcher = PipeJsonProtocol.CancelOnDisconnectAsync(server, request, stop.Token);
        stop.Cancel();
        await watcher.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(request.IsCancellationRequested);
        Assert.True(server.IsConnected);
    }
}
