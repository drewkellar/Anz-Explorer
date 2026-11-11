using AnzExplorer.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace AnzExplorer.App;

internal static class FileTransferService
{
    public static async Task<IReadOnlyList<IStorageItem>> GetStorageItemsAsync(
        IEnumerable<FileSystemItemViewModel> sourceItems)
    {
        var storageItems = new List<IStorageItem>();
        foreach (var source in sourceItems)
        {
            storageItems.Add(source.IsFolder
                ? await StorageFolder.GetFolderFromPathAsync(source.Path)
                : await StorageFile.GetFileFromPathAsync(source.Path));
        }

        return storageItems;
    }

    public static async Task PutOnClipboardAsync(
        IEnumerable<FileSystemItemViewModel> sourceItems,
        DataPackageOperation operation)
    {
        var storageItems = await GetStorageItemsAsync(sourceItems);
        if (storageItems.Count == 0) return;

        var package = new DataPackage { RequestedOperation = operation };
        package.SetStorageItems(storageItems, false);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    public static async Task<IReadOnlyList<string>> PasteAsync(
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.StorageItems)) return [];

        var sourceItems = await content.GetStorageItemsAsync();
        var operation = content.RequestedOperation == DataPackageOperation.Move
            ? DataPackageOperation.Move
            : DataPackageOperation.Copy;
        var failures = await TransferAsync(sourceItems, destinationDirectory, operation, cancellationToken);
        // Retain cut data: Shell conflict UI can skip individual items, and the
        // clipboard may also have been replaced while the transfer was running.
        return failures;
    }

    public static Task<IReadOnlyList<string>> TransferAsync(
        IEnumerable<IStorageItem> sourceItems,
        string destinationDirectory,
        DataPackageOperation operation,
        CancellationToken cancellationToken) =>
        ShellTransferEngine.TransferAsync(
            sourceItems.Select(item => new FileTransferSource(
                item.Path,
                item.Name,
                item is StorageFolder)),
            destinationDirectory,
            operation == DataPackageOperation.Move ? FileTransferMode.Move : FileTransferMode.Copy,
            cancellationToken);
}
