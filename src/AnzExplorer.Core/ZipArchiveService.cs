using System.IO.Compression;

namespace AnzExplorer.Core;

public static class ZipArchiveService
{
    public static Task<string> CreateAsync(
        IEnumerable<FileTransferSource> sourceItems,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        var sources = sourceItems.ToArray();
        return Task.Run(
            () => Create(sources, destinationDirectory, cancellationToken),
            cancellationToken);
    }

    private static string Create(
        IReadOnlyList<FileTransferSource> sources,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        if (sources.Count == 0) throw new ArgumentException("Select at least one item to compress.", nameof(sources));
        if (!Directory.Exists(destinationDirectory)) throw new DirectoryNotFoundException(destinationDirectory);

        var archiveName = sources.Count == 1
            ? GetArchiveBaseName(sources[0])
            : "Archive";
        var archivePath = GetUniqueArchivePath(destinationDirectory, archiveName);

        try
        {
            using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false);
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (source.IsFolder)
                {
                    var rootName = GetSafeEntryName(source.Name, source.Path);
                    AddDirectory(archive, source.Path, rootName, cancellationToken);
                }
                else
                {
                    AddFile(
                        archive,
                        source.Path,
                        GetSafeEntryName(source.Name, source.Path),
                        cancellationToken);
                }
            }

            return archivePath;
        }
        catch
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);
            throw;
        }
    }

    private static void AddDirectory(
        ZipArchive archive,
        string directoryPath,
        string entryPath,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directoryPath)) throw new DirectoryNotFoundException(directoryPath);
        var normalizedEntry = NormalizeEntryPath(entryPath).TrimEnd('/') + "/";
        archive.CreateEntry(normalizedEntry);

        foreach (var file in Directory.EnumerateFiles(directoryPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
            AddFile(archive, file, normalizedEntry + Path.GetFileName(file), cancellationToken);
        }

        foreach (var directory in Directory.EnumerateDirectories(directoryPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            AddDirectory(
                archive,
                directory,
                normalizedEntry + Path.GetFileName(directory),
                cancellationToken);
        }
    }

    private static void AddFile(
        ZipArchive archive,
        string filePath,
        string entryPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("The selected file is unavailable.", filePath);
        var entry = archive.CreateEntry(NormalizeEntryPath(entryPath), CompressionLevel.Optimal);
        using var source = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var destination = entry.Open();
        var buffer = new byte[128 * 1024];
        int bytesRead;
        while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            destination.Write(buffer, 0, bytesRead);
        }
    }

    private static string GetArchiveBaseName(FileTransferSource source)
    {
        var name = GetSafeEntryName(source.Name, source.Path);
        var baseName = source.IsFolder ? name : Path.GetFileNameWithoutExtension(name);
        return string.IsNullOrWhiteSpace(baseName) ? "Archive" : baseName;
    }

    private static string GetSafeEntryName(string name, string path)
    {
        var safeName = Path.GetFileName(Path.TrimEndingDirectorySeparator(name));
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (string.IsNullOrWhiteSpace(safeName)) throw new IOException("An archive entry name could not be determined.");
        return safeName;
    }

    private static string GetUniqueArchivePath(string directory, string baseName)
    {
        var candidate = Path.Combine(directory, baseName + ".zip");
        if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;

        for (var copy = 2; copy < 10_000; copy++)
        {
            candidate = Path.Combine(directory, $"{baseName} ({copy}).zip");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }

        throw new IOException("No available ZIP archive name could be generated.");
    }

    private static string NormalizeEntryPath(string path) => path.Replace('\\', '/').TrimStart('/');
}
