using System.Text.Json;
using AnzExplorer.Contracts;
using AnzExplorer.Core;

namespace AnzExplorer.Core.Tests;

public sealed class PipeJsonProtocolTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ReadAsync_CompletesWithoutEndOfStreamAndPreservesConsecutiveMessages()
    {
        var first = new GetFolderSizeRequest(Guid.NewGuid(), @"C:\One");
        var second = new PauseIndexingRequest(Guid.NewGuid());
        await using var stream = new MemoryStream();

        await PipeJsonProtocol.WriteAsync<IndexRequest>(stream, first, JsonOptions, CancellationToken.None);
        await PipeJsonProtocol.WriteAsync<IndexRequest>(stream, second, JsonOptions, CancellationToken.None);
        stream.Position = 0;

        var firstRead = await PipeJsonProtocol.ReadAsync<IndexRequest>(stream, JsonOptions, CancellationToken.None);
        var secondRead = await PipeJsonProtocol.ReadAsync<IndexRequest>(stream, JsonOptions, CancellationToken.None);

        var sizeRequest = Assert.IsType<GetFolderSizeRequest>(firstRead);
        Assert.Equal(first.Path, sizeRequest.Path);
        Assert.IsType<PauseIndexingRequest>(secondRead);
    }

    [Fact]
    public async Task ReadAsync_RejectsOversizedFrameBeforeAllocatingPayload()
    {
        await using var stream = new MemoryStream(BitConverter.GetBytes(2 * 1024 * 1024));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => PipeJsonProtocol.ReadAsync<IndexResponse>(stream, JsonOptions, CancellationToken.None));
    }
}
