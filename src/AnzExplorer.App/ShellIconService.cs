using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace AnzExplorer.App;

/// <summary>
/// Resolves the same registered file icons and thumbnails used by Windows Shell.
/// Only realized rows request an icon, and reusable file-type icons are cached.
/// </summary>
internal sealed class ShellIconService
{
    private const int RequestedIconSize = 128;
    private const int RequestedImagePreviewSize = 256;
    private const int MaximumCachedFileTypes = 256;
    private static readonly TimeSpan ShellRequestTimeout = TimeSpan.FromSeconds(3);
    private readonly Dictionary<string, ImageSource> _fileTypeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (byte[] Pixels, int Width, int Height)?> _locationCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _concurrency = new(6, 6);

    public async Task<ImageSource?> GetIconAsync(FileSystemItemViewModel item)
    {
        if (item.IsFolder || item.IsDrive || string.IsNullOrWhiteSpace(item.Path)) return null;

        var extension = Path.GetExtension(item.Path);
        var cacheKey = string.IsNullOrEmpty(extension) ? "<no-extension>" : extension;
        if (CanCacheByFileType(extension) && _fileTypeCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        await _concurrency.WaitAsync();
        try
        {
            var isImage = item.IsImageFile;
            var requestedSize = (uint)(isImage ? RequestedImagePreviewSize : RequestedIconSize);
            var file = await StorageFile.GetFileFromPathAsync(item.Path)
                .AsTask()
                .WaitAsync(ShellRequestTimeout);
            if (isImage)
            {
                using var stream = await file.OpenReadAsync()
                    .AsTask()
                    .WaitAsync(ShellRequestTimeout);
                var preview = new BitmapImage
                {
                    DecodePixelWidth = RequestedImagePreviewSize,
                    DecodePixelType = DecodePixelType.Logical
                };
                await preview.SetSourceAsync(stream)
                    .AsTask()
                    .WaitAsync(ShellRequestTimeout);
                return preview;
            }
            using var thumbnail = await file.GetThumbnailAsync(
                    ThumbnailMode.ListView,
                    requestedSize,
                    // UseCurrentScale and ResizeThumbnail are mutually exclusive; combining them
                    // made every request throw, so file-type icons never loaded.
                    ThumbnailOptions.UseCurrentScale)
                .AsTask()
                .WaitAsync(ShellRequestTimeout);

            if (thumbnail is null || thumbnail.Size == 0) return null;

            var image = new BitmapImage
            {
                DecodePixelWidth = (int)requestedSize,
                DecodePixelType = DecodePixelType.Logical
            };
            await image.SetSourceAsync(thumbnail)
                .AsTask()
                .WaitAsync(ShellRequestTimeout);

            // Shell icons are reusable for a registered file type. Image thumbnails and
            // executable/shortcut icons are item-specific and must never be shared.
            if (thumbnail.Type == ThumbnailType.Icon
                && CanCacheByFileType(extension)
                && _fileTypeCache.Count < MaximumCachedFileTypes)
            {
                _fileTypeCache[cacheKey] = image;
            }

            return image;
        }
        catch (Exception)
        {
            // Icon providers include third-party Shell extensions. A broken or slow
            // provider must never interrupt file enumeration or crash the window.
            return null;
        }
        finally
        {
            _concurrency.Release();
        }
    }

    /// <summary>
    /// Returns the icon File Explorer shows for a drive or network location (for example
    /// the Windows logo on the system drive). Network locations use their local shortcut
    /// folder, so an offline share is never contacted.
    /// </summary>
    public async Task<ImageSource?> GetLocationIconAsync(FileSystemItemViewModel item)
    {
        var source = item.IconSourcePath ?? item.Path;
        if (string.IsNullOrWhiteSpace(source)) return null;

        // Pixels are cached rather than image objects: a shared image source did not
        // render again after the tiles were recreated on a later visit to This PC.
        if (!_locationCache.TryGetValue(source, out var icon))
        {
            await _concurrency.WaitAsync();
            try
            {
                icon = await Task.Run(() => ShellItemImage.GetIconPixels(source, RequestedIconSize))
                    .WaitAsync(ShellRequestTimeout);
            }
            catch (Exception)
            {
                // Not-ready drives and unreachable mapped drives fall back to the glyph.
                return null;
            }
            finally
            {
                _concurrency.Release();
            }

            if (icon is null) return null;
            if (_locationCache.Count < MaximumCachedFileTypes) _locationCache[source] = icon.Value;
        }

        if (icon is not { } pixels) return null;
        var bitmap = new WriteableBitmap(pixels.Width, pixels.Height);
        using (var buffer = bitmap.PixelBuffer.AsStream())
        {
            await buffer.WriteAsync(pixels.Pixels);
        }
        bitmap.Invalidate();
        return bitmap;
    }

    private static bool CanCacheByFileType(string extension) =>
        !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
        && !extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
        && !extension.Equals(".url", StringComparison.OrdinalIgnoreCase)
        && !extension.Equals(".ico", StringComparison.OrdinalIgnoreCase);

}
