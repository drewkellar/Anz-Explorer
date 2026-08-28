namespace AnzExplorer.Core;

public enum FileTransferMode
{
    Copy,
    Move
}

public sealed record FileTransferSource(string Path, string Name, bool IsFolder);

public static class FileTransferEngine
{
    public static Task<IReadOnlyList<string>> TransferAsync(
        IEnumerable<FileTransferSource> sourceItems,
        string destinationDirectory,
        FileTransferMode mode,
        CancellationToken cancellationToken)
    {
        var sources = sourceItems.ToArray();
        return Task.Run<IReadOnlyList<string>>(
            () => Transfer(sources, destinationDirectory, mode, cancellationToken),
            cancellationToken);
    }

    private static IReadOnlyList<string> Transfer(
        IReadOnlyList<FileTransferSource> sourceItems,
        string destinationDirectory,
        FileTransferMode mode,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        if (!Directory.Exists(destinationDirectory))
        {
            return [$"The destination folder is unavailable: {destinationDirectory}"];
        }

        foreach (var source in sourceItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                TransferOne(source, destinationDirectory, mode, cancellationToken);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or NotSupportedException)
            {
                failures.Add($"{source.Name}: {exception.Message}");
            }
        }

        return failures;
    }

    private static void TransferOne(
        FileTransferSource source,
        string destinationDirectory,
        FileTransferMode mode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.Path))
        {
            throw new IOException("This item does not expose a filesystem path.");
        }

        var sourcePath = Path.GetFullPath(source.Path);
        var directDestination = Path.Combine(destinationDirectory, source.Name);
        var moving = mode == FileTransferMode.Move;

        if (moving && PathsEqual(sourcePath, directDestination)) return;
        if (source.IsFolder && IsSamePathOrDescendant(destinationDirectory, sourcePath))
        {
            throw new IOException("A folder cannot be copied or moved into itself.");
        }

        var destination = GetUniqueDestinationPath(destinationDirectory, source.Name, source.IsFolder);
        if (source.IsFolder)
        {
            if (moving) MoveDirectory(sourcePath, destination, cancellationToken);
            else CopyDirectory(sourcePath, destination, cancellationToken);
        }
        else if (moving)
        {
            MoveFile(sourcePath, destination);
        }
        else
        {
            File.Copy(sourcePath, destination);
        }
    }

    private static void MoveFile(string source, string destination)
    {
        if (IsSameVolume(source, destination))
        {
            File.Move(source, destination);
            return;
        }

        File.Copy(source, destination);
        File.Delete(source);
    }

    private static void MoveDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        if (IsSameVolume(source, destination))
        {
            Directory.Move(source, destination);
            return;
        }

        CopyDirectory(source, destination, cancellationToken);
        Directory.Delete(source, true);
    }

    private static bool IsSameVolume(string first, string second) => string.Equals(
        Path.GetPathRoot(Path.GetFullPath(first)),
        Path.GetPathRoot(Path.GetFullPath(second)),
        StringComparison.OrdinalIgnoreCase);

    private static bool PathsEqual(string first, string second) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
        StringComparison.OrdinalIgnoreCase);

    private static bool IsSamePathOrDescendant(string candidate, string parent)
    {
        var canonicalCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var canonicalParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return PathsEqual(canonicalCandidate, canonicalParent)
            || canonicalCandidate.StartsWith(
                canonicalParent + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string GetUniqueDestinationPath(string directory, string name, bool isFolder)
    {
        var candidate = Path.Combine(directory, name);
        if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;

        var baseName = isFolder ? name : Path.GetFileNameWithoutExtension(name);
        var extension = isFolder ? string.Empty : Path.GetExtension(name);
        for (var copy = 2; copy < 10_000; copy++)
        {
            candidate = Path.Combine(directory, $"{baseName} ({copy}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }

        throw new IOException("No available destination name could be generated.");
    }

    private static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)), cancellationToken);
        }
    }
}
