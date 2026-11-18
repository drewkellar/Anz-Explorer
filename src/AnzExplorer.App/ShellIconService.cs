using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AnzExplorer.App;

/// <summary>
/// Resolves the same registered file icons and thumbnails used by Windows Shell.
/// Only realized rows request an icon, and reusable file-type icons are cached.
/// </summary>
/// <remarks>
/// Images are read through IShellItemImageFactory on a background thread and copied
/// into a <see cref="WriteableBitmap"/>. Decoding StorageFile thumbnail streams with
/// BitmapImage.SetSourceAsync crashed the process inside WinUI (stowed E_UNEXPECTED)
/// on roughly one launch in five, even for images that were never displayed.
/// </remarks>
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
        var isImage = item.IsImageFile;
        // Shell icons are reusable for a registered file type. Image previews and
        // executable/shortcut icons are item-specific and must never be shared.
        var cacheable = !isImage && CanCacheByFileType(extension);
        if (cacheable && _fileTypeCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        (byte[] Pixels, int Width, int Height)? pixels;
        await _concurrency.WaitAsync();
        try
        {
            var path = item.Path;
            var size = isImage ? RequestedImagePreviewSize : RequestedIconSize;
            pixels = await Task.Run(async () =>
                    (isImage ? await TryDecodePreviewAsync(path, size) : null)
                    ?? ShellItemImage.GetIconPixels(path, size))
                .WaitAsync(ShellRequestTimeout);
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

        if (pixels is not { } image) return null;
        if (cacheable && _fileTypeCache.TryGetValue(cacheKey, out cached)) return cached;

        var bitmap = await CreateBitmapAsync(image);
        if (cacheable && _fileTypeCache.Count < MaximumCachedFileTypes)
        {
            _fileTypeCache[cacheKey] = bitmap;
        }

        return bitmap;
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
        return await CreateBitmapAsync(pixels);
    }

    /// <summary>
    /// Decodes a downscaled preview of an image file off the UI thread. Returns null for
    /// formats without an installed decoder (such as SVG), which then show their icon.
    /// </summary>
    private static async Task<(byte[] Pixels, int Width, int Height)?> TryDecodePreviewAsync(string path, int size)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            var width = decoder.OrientedPixelWidth;
            var height = decoder.OrientedPixelHeight;
            if (width == 0 || height == 0) return null;

            var scale = Math.Min(1.0, (double)size / Math.Max(width, height));
            var targetWidth = (uint)Math.Max(1, Math.Round(width * scale));
            var targetHeight = (uint)Math.Max(1, Math.Round(height * scale));
            // Scaling applies before EXIF rotation, so swap the axes for rotated photos.
            var rotated = decoder.OrientedPixelWidth != decoder.PixelWidth;
            var transform = new Windows.Graphics.Imaging.BitmapTransform
            {
                ScaledWidth = rotated ? targetHeight : targetWidth,
                ScaledHeight = rotated ? targetWidth : targetHeight,
                InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant
            };
            var data = await decoder.GetPixelDataAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                transform,
                Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.ColorManageToSRgb);
            return (data.DetachPixelData(), (int)targetWidth, (int)targetHeight);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<WriteableBitmap> CreateBitmapAsync((byte[] Pixels, int Width, int Height) image)
    {
        var bitmap = new WriteableBitmap(image.Width, image.Height);
        using (var buffer = bitmap.PixelBuffer.AsStream())
        {
            await buffer.WriteAsync(image.Pixels);
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
