using AnzExplorer.Core;

namespace AnzExplorer.Core.Tests;

public sealed class FileTransferEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"anz-transfer-{Guid.NewGuid():N}");

    public FileTransferEngineTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Copy_preserves_files_and_nested_folders()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source"));
        var nested = source.CreateSubdirectory("nested");
        await File.WriteAllTextAsync(Path.Combine(source.FullName, "one.txt"), "one");
        await File.WriteAllTextAsync(Path.Combine(nested.FullName, "two.txt"), "two");
        var destination = Directory.CreateDirectory(Path.Combine(_root, "destination"));

        var failures = await FileTransferEngine.TransferAsync(
            [new FileTransferSource(source.FullName, source.Name, true)],
            destination.FullName,
            FileTransferMode.Copy,
            CancellationToken.None);

        Assert.Empty(failures);
        Assert.Equal("one", await File.ReadAllTextAsync(Path.Combine(destination.FullName, "source", "one.txt")));
        Assert.Equal("two", await File.ReadAllTextAsync(Path.Combine(destination.FullName, "source", "nested", "two.txt")));
        Assert.True(source.Exists);
    }

    [Fact]
    public async Task Copy_collision_creates_numbered_name_without_overwriting()
    {
        var sourceDirectory = Directory.CreateDirectory(Path.Combine(_root, "source"));
        var source = Path.Combine(sourceDirectory.FullName, "report.txt");
        await File.WriteAllTextAsync(source, "new");
        var destination = Directory.CreateDirectory(Path.Combine(_root, "destination"));
        await File.WriteAllTextAsync(Path.Combine(destination.FullName, "report.txt"), "existing");

        var failures = await FileTransferEngine.TransferAsync(
            [new FileTransferSource(source, "report.txt", false)],
            destination.FullName,
            FileTransferMode.Copy,
            CancellationToken.None);

        Assert.Empty(failures);
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(destination.FullName, "report.txt")));
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(destination.FullName, "report (2).txt")));
    }

    [Fact]
    public async Task Move_removes_source_only_after_destination_succeeds()
    {
        var sourceDirectory = Directory.CreateDirectory(Path.Combine(_root, "source"));
        var source = Path.Combine(sourceDirectory.FullName, "move-me.txt");
        await File.WriteAllTextAsync(source, "payload");
        var destination = Directory.CreateDirectory(Path.Combine(_root, "destination"));

        var failures = await FileTransferEngine.TransferAsync(
            [new FileTransferSource(source, "move-me.txt", false)],
            destination.FullName,
            FileTransferMode.Move,
            CancellationToken.None);

        Assert.Empty(failures);
        Assert.False(File.Exists(source));
        Assert.Equal("payload", await File.ReadAllTextAsync(Path.Combine(destination.FullName, "move-me.txt")));
    }

    [Fact]
    public async Task Folder_move_places_the_folder_inside_the_target_folder()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "dragged-folder"));
        await File.WriteAllTextAsync(Path.Combine(source.FullName, "inside.txt"), "payload");
        var target = Directory.CreateDirectory(Path.Combine(_root, "target-folder"));

        var failures = await FileTransferEngine.TransferAsync(
            [new FileTransferSource(source.FullName, source.Name, true)],
            target.FullName,
            FileTransferMode.Move,
            CancellationToken.None);

        Assert.Empty(failures);
        Assert.False(Directory.Exists(source.FullName));
        Assert.Equal(
            "payload",
            await File.ReadAllTextAsync(Path.Combine(target.FullName, "dragged-folder", "inside.txt")));
    }

    [Fact]
    public async Task Folder_cannot_be_transferred_into_its_descendant()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source"));
        var descendant = source.CreateSubdirectory("descendant");
        await File.WriteAllTextAsync(Path.Combine(source.FullName, "keep.txt"), "safe");

        var failures = await FileTransferEngine.TransferAsync(
            [new FileTransferSource(source.FullName, source.Name, true)],
            descendant.FullName,
            FileTransferMode.Move,
            CancellationToken.None);

        Assert.Single(failures);
        Assert.True(File.Exists(Path.Combine(source.FullName, "keep.txt")));
        Assert.False(Directory.Exists(Path.Combine(descendant.FullName, source.Name)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
