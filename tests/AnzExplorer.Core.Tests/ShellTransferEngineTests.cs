using AnzExplorer.Core;

namespace AnzExplorer.Core.Tests;

public sealed class ShellTransferEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "anz-shell-test-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(FileTransferMode.Copy)]
    [InlineData(FileTransferMode.Move)]
    public async Task Native_transfer_preserves_nested_unicode_content(FileTransferMode mode)
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source"));
        var nested = source.CreateSubdirectory("日本語");
        await File.WriteAllTextAsync(Path.Combine(nested.FullName, "payload.txt"), "complete payload");
        var target = Directory.CreateDirectory(Path.Combine(_root, "target"));
        var errors = await ShellTransferEngine.TransferAsync(
            [new(source.FullName, source.Name, true)], target.FullName, mode, CancellationToken.None);
        Assert.Empty(errors);
        Assert.Equal("complete payload", await File.ReadAllTextAsync(Path.Combine(target.FullName, "source", "日本語", "payload.txt")));
        Assert.Equal(mode == FileTransferMode.Copy, Directory.Exists(source.FullName));
    }

    [Fact]
    public async Task Self_descendant_move_is_rejected_without_mutation()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source"));
        var target = source.CreateSubdirectory("child");
        var errors = await ShellTransferEngine.TransferAsync(
            [new(source.FullName, source.Name, true)], target.FullName, FileTransferMode.Move, CancellationToken.None);
        Assert.Single(errors);
        Assert.True(Directory.Exists(source.FullName));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target.FullName));
    }

    [Fact]
    public async Task Cancellation_before_start_preserves_source()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source"));
        var target = Directory.CreateDirectory(Path.Combine(_root, "target"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ShellTransferEngine.TransferAsync(
            [new(source.FullName, source.Name, true)], target.FullName, FileTransferMode.Move, new CancellationToken(true)));
        Assert.True(Directory.Exists(source.FullName));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target.FullName));
    }

    [Fact]
    public async Task Duplicate_in_place_preserves_original_and_creates_second_file()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "original.txt");
        await File.WriteAllTextAsync(source, "unchanged");
        var errors = await ShellTransferEngine.TransferAsync(
            [new(source, "original.txt", false)], _root, FileTransferMode.Copy, CancellationToken.None);
        Assert.Empty(errors);
        Assert.Equal("unchanged", await File.ReadAllTextAsync(source));
        var files = Directory.GetFiles(_root);
        Assert.Equal(2, files.Length);
        foreach (var file in files) Assert.Equal("unchanged", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task Permanent_delete_removes_files_and_nested_folders_in_one_operation()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "folder"));
        await File.WriteAllTextAsync(Path.Combine(folder.CreateSubdirectory("nested").FullName, "a.txt"), "a");
        var file = Path.Combine(_root, "file.txt");
        await File.WriteAllTextAsync(file, "b");
        var keep = Path.Combine(_root, "keep.txt");
        await File.WriteAllTextAsync(keep, "c");

        var errors = await ShellTransferEngine.DeleteAsync([folder.FullName, file], recycle: false, CancellationToken.None);

        Assert.Empty(errors);
        Assert.False(Directory.Exists(folder.FullName));
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public async Task Delete_reports_missing_item_and_still_deletes_the_rest()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "file.txt");
        await File.WriteAllTextAsync(file, "b");

        var errors = await ShellTransferEngine.DeleteAsync(
            [Path.Combine(_root, "missing.txt"), file], recycle: false, CancellationToken.None);

        Assert.Single(errors);
        Assert.False(File.Exists(file));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
