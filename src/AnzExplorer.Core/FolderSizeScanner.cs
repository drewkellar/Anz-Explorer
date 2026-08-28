using AnzExplorer.Contracts;
using System.ComponentModel;

namespace AnzExplorer.Core;

public sealed class FolderSizeScanner
{
    public async Task<FolderSizeResult> ScanAsync(
        string path,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var canonicalPath = PathCanonicalizer.Canonicalize(path);
        if (!Directory.Exists(canonicalPath))
        {
            return new FolderSizeResult(
                canonicalPath, null, FolderSizeState.Unavailable, DateTimeOffset.UtcNow, "Folder is unavailable.");
        }

        return await Task.Run(
            () => ScanCore(canonicalPath, progress, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static FolderSizeResult ScanCore(
        string root,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        long total = 0;
        var partial = false;
        var inaccessibleCount = 0;
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            try
            {
                foreach (var entry in NativeDirectoryEnumerator.Enumerate(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if ((entry.Attributes & FileAttributes.Directory) != 0)
                        {
                            if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
                            {
                                pending.Push(Path.Combine(directory, entry.Name));
                            }

                            continue;
                        }

                        total = checked(total + entry.Length);
                        progress?.Report(total);
                    }
                    catch (Exception exception) when (IsRecoverable(exception))
                    {
                        partial = true;
                        inaccessibleCount++;
                    }
                    catch (OverflowException)
                    {
                        return new FolderSizeResult(
                            root, long.MaxValue, FolderSizeState.Partial, DateTimeOffset.UtcNow, "Folder size exceeded the supported range.");
                    }
                }
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                partial = true;
                inaccessibleCount++;
            }
        }

        return new FolderSizeResult(
            root,
            total,
            partial ? FolderSizeState.Partial : FolderSizeState.Complete,
            DateTimeOffset.UtcNow,
            partial ? $"{inaccessibleCount} item(s) could not be read." : null);
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is UnauthorizedAccessException
            or IOException
            or PathTooLongException
            or DirectoryNotFoundException
            or FileNotFoundException
            or NotSupportedException
            or Win32Exception;
}
