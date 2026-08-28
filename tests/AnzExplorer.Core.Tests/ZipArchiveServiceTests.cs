using System.IO.Compression;
using AnzExplorer.Core;

namespace AnzExplorer.Core.Tests;

public sealed class ZipArchiveServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"anz-zip-{Guid.NewGuid():N}");

    public ZipArchiveServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Folder_compression_preserves_root_nested_files_and_empty_directories()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "Project"));
        var nested = folder.CreateSubdirectory("nested");
        folder.CreateSubdirectory("empty");
        await File.WriteAllTextAsync(Path.Combine(folder.FullName, "one.txt"), "one");
        await File.WriteAllTextAsync(Path.Combine(nested.FullName, "two.txt"), "two");

        var archivePath = await ZipArchiveService.CreateAsync(
            [new FileTransferSource(folder.FullName, folder.Name, true)],
            _root,
            CancellationToken.None);

        Assert.Equal(Path.Combine(_root, "Project.zip"), archivePath);
        using var archive = ZipFile.OpenRead(archivePath);
        Assert.Contains(archive.Entries, entry => entry.FullName == "Project/one.txt");
        Assert.Contains(archive.Entries, entry => entry.FullName == "Project/nested/two.txt");
        Assert.Contains(archive.Entries, entry => entry.FullName == "Project/empty/");
    }

    [Fact]
    public async Task Existing_archive_is_not_overwritten()
    {
        var source = Path.Combine(_root, "report.txt");
        await File.WriteAllTextAsync(source, "new");
        await File.WriteAllTextAsync(Path.Combine(_root, "report.zip"), "existing");

        var archivePath = await ZipArchiveService.CreateAsync(
            [new FileTransferSource(source, "report.txt", false)],
            _root,
            CancellationToken.None);

        Assert.Equal(Path.Combine(_root, "report (2).zip"), archivePath);
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(_root, "report.zip")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
