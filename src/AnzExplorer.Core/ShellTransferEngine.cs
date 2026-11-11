using System.Runtime.InteropServices;

namespace AnzExplorer.Core;

/// <summary>Runs Shell transfers on an STA, away from the presentation thread.</summary>
public static class ShellTransferEngine
{
    private static readonly SemaphoreSlim Queue = new(1);

    public static async Task<IReadOnlyList<string>> TransferAsync(
        IEnumerable<FileTransferSource> sourceItems, string destinationDirectory,
        FileTransferMode mode, CancellationToken cancellationToken)
    {
        var sources = sourceItems.ToArray();
        return await RunSerializedOnStaAsync(
            () => Transfer(sources, destinationDirectory, mode, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Deletes items with one native Shell operation: off the UI thread, with Shell progress
    /// and error UI. When <paramref name="recycle"/> is true, items go to the Recycle Bin and
    /// the Shell still warns before any item that cannot be recycled is destroyed.
    /// </summary>
    public static async Task<IReadOnlyList<string>> DeleteAsync(
        IEnumerable<string> paths, bool recycle, CancellationToken cancellationToken)
    {
        var targets = paths.ToArray();
        return await RunSerializedOnStaAsync(() => Delete(targets, recycle, cancellationToken), cancellationToken);
    }

    private static async Task<IReadOnlyList<string>> RunSerializedOnStaAsync(
        Func<IReadOnlyList<string>> work, CancellationToken cancellationToken)
    {
        await Queue.WaitAsync(cancellationToken);
        try
        {
            var completion = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { completion.SetResult(work()); }
                catch (OperationCanceledException) { completion.SetCanceled(cancellationToken); }
                catch (Exception error) { completion.SetException(error); }
            }) { IsBackground = true, Name = "Anz Shell file operations" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await completion.Task;
        }
        finally { Queue.Release(); }
    }

    private const uint FofNoConfirmation = 0x10;
    private const uint FofAllowUndo = 0x40;
    private const uint FofWantNukeWarning = 0x4000;
    private const uint FofxRecycleOnDelete = 0x80000;

    private static IReadOnlyList<string> Delete(string[] paths, bool recycle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var failures = new List<string>();
        var items = new List<IShellItem>();
        object? operationObject = null;
        try
        {
            operationObject = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), true)!)!;
            var operation = (IFileOperation)operationObject;
            // The app has already confirmed (or the user opted out of confirmation), so
            // suppress the Shell's own prompt. FOF_WANTNUKEWARNING keeps the warning when a
            // "recycle" would actually destroy the item (e.g. too large for the Recycle Bin).
            operation.SetOperationFlags(recycle
                ? FofAllowUndo | FofxRecycleOnDelete | FofNoConfirmation | FofWantNukeWarning
                : FofNoConfirmation);

            foreach (var path in paths)
            {
                try
                {
                    var item = CreateItem(Path.GetFullPath(path));
                    items.Add(item);
                    operation.DeleteItem(item, IntPtr.Zero);
                }
                catch (Exception error) when (error is COMException or IOException or ArgumentException)
                {
                    failures.Add($"{Path.GetFileName(path)}: {error.Message}");
                }
            }

            if (items.Count == 0) return failures;
            var result = operation.PerformOperations();
            operation.GetAnyOperationsAborted(out var aborted);
            if (aborted)
            {
                failures.Add("Delete stopped before all items were removed.");
                return failures;
            }
            Marshal.ThrowExceptionForHR(result);
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException)
        {
            failures.Add(error.Message);
        }
        finally
        {
            foreach (var item in items) Marshal.FinalReleaseComObject(item);
            if (operationObject is not null) Marshal.FinalReleaseComObject(operationObject);
        }

        return failures;
    }

    private static IReadOnlyList<string> Transfer(FileTransferSource[] sources, string destination,
        FileTransferMode mode, CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (!Directory.Exists(destination)) return ["The destination folder is unavailable."];
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            object? operationObject = null;
            IShellItem? sourceItem = null;
            IShellItem? destinationItem = null;
            try
            {
                var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Path));
                if (source.IsFolder && (string.Equals(path, destination, StringComparison.OrdinalIgnoreCase)
                    || destination.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("A folder cannot be copied or moved into itself.");
                var sameParent = string.Equals(Path.GetDirectoryName(path), destination, StringComparison.OrdinalIgnoreCase);
                if (sameParent && mode == FileTransferMode.Move) continue;

                operationObject = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), true)!)!;
                var operation = (IFileOperation)operationObject;
                // Allow undo; preserve normal Shell confirmation, progress and collision UI.
                // Duplicate-in-place must generate a new name rather than overwrite its own source.
                operation.SetOperationFlags(0x40u | 0x20000000u | (sameParent ? 0x8u : 0u));
                sourceItem = CreateItem(path);
                destinationItem = CreateItem(destination);
                if (mode == FileTransferMode.Move) operation.MoveItem(sourceItem, destinationItem, null, IntPtr.Zero);
                else operation.CopyItem(sourceItem, destinationItem, null, IntPtr.Zero);
                var result = operation.PerformOperations();
                // S_OK can still mean cancellation. Always inspect the aborted flag.
                operation.GetAnyOperationsAborted(out var aborted);
                if (aborted)
                {
                    failures.Add("Transfer stopped before all items completed. Remaining items were not processed.");
                    break;
                }
                Marshal.ThrowExceptionForHR(result);
            }
            catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                failures.Add($"{source.Name}: {error.Message}");
            }
            finally
            {
                if (destinationItem is not null) Marshal.FinalReleaseComObject(destinationItem);
                if (sourceItem is not null) Marshal.FinalReleaseComObject(sourceItem);
                if (operationObject is not null) Marshal.FinalReleaseComObject(operationObject);
            }
        }
        return failures;
    }

    private static IShellItem CreateItem(string path)
    {
        var iid = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item));
        return item;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid,
        out IShellItem item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem { }

    // Method order must exactly match shobjidl_core.h, including unused slots.
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(IntPtr hwnd);
        void ApplyPropertiesToItem(IntPtr item);
        void ApplyPropertiesToItems(IntPtr items);
        void RenameItem(IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IShellItem item, IShellItem destination,
            [MarshalAs(UnmanagedType.LPWStr)] string? name, IntPtr sink);
        void MoveItems(IntPtr items, IntPtr destination);
        void CopyItem(IShellItem item, IShellItem destination,
            [MarshalAs(UnmanagedType.LPWStr)] string? name, IntPtr sink);
        void CopyItems(IntPtr items, IntPtr destination);
        void DeleteItem(IShellItem item, IntPtr sink);
        void DeleteItems(IntPtr items);
        void NewItem(IntPtr destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
        [PreserveSig] int PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
