using AnzExplorer.Contracts;
using AnzExplorer.Core;

namespace AnzExplorer.Core.Tests;

public sealed class FolderSizeScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AnzExplorerTests", Guid.NewGuid().ToString("N"));

    public FolderSizeScannerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ScanAsync_AggregatesNestedLogicalFileLengths()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_root, "nested"));
        await File.WriteAllBytesAsync(Path.Combine(_root, "one.bin"), new byte[11]);
        await File.WriteAllBytesAsync(Path.Combine(nested.FullName, "two.bin"), new byte[29]);

        var result = await new FolderSizeScanner().ScanAsync(_root);

        Assert.Equal(FolderSizeState.Complete, result.State);
        Assert.Equal(40, result.LogicalBytes);
    }

    [Fact]
    public async Task ScanAsync_ReportsUnavailableForMissingFolder()
    {
        var missing = Path.Combine(_root, "missing");
        var result = await new FolderSizeScanner().ScanAsync(missing);
        Assert.Equal(FolderSizeState.Unavailable, result.State);
        Assert.Null(result.LogicalBytes);
    }

    [Fact]
    public async Task ScanAsync_ObservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new FolderSizeScanner().ScanAsync(_root, cancellationToken: cancellation.Token));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}

