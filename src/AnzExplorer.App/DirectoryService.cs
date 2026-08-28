using AnzExplorer.Contracts;

namespace AnzExplorer.App;

internal sealed class DirectoryService
{
    public Task<IReadOnlyList<FileSystemItemViewModel>> EnumerateAsync(
        string path,
        string? searchText,
        CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<FileSystemItemViewModel>>(
            () => Enumerate(path, searchText, cancellationToken),
            cancellationToken);

    private static IReadOnlyList<FileSystemItemViewModel> Enumerate(
        string path,
        string? searchText,
        CancellationToken cancellationToken)
    {
        var results = new List<FileSystemItemViewModel>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(entry);
            if (!string.IsNullOrWhiteSpace(searchText)
                && !name.Contains(searchText, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var attributes = File.GetAttributes(entry);
                var isFolder = (attributes & FileAttributes.Directory) != 0;
                if (isFolder)
                {
                    var info = new DirectoryInfo(entry);
                    results.Add(new FileSystemItemViewModel
                    {
                        Name = name,
                        Path = entry,
                        IsFolder = true,
                        Modified = info.LastWriteTimeUtc,
                        Type = "File folder",
                        SizeState = FolderSizeState.Queued
                    });
                }
                else
                {
                    var info = new FileInfo(entry);
                    results.Add(new FileSystemItemViewModel
                    {
                        Name = name,
                        Path = entry,
                        IsFolder = false,
                        Modified = info.LastWriteTimeUtc,
                        Type = string.IsNullOrEmpty(info.Extension) ? "File" : $"{info.Extension.TrimStart('.').ToUpperInvariant()} file",
                        LogicalBytes = info.Length,
                        SizeState = FolderSizeState.Complete
                    });
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FileNotFoundException)
            {
                // Items that disappear or become inaccessible during enumeration are omitted.
            }
        }

        return results
            .OrderByDescending(item => item.IsFolder)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }
}
