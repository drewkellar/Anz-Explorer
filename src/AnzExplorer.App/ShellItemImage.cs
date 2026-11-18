using System.Runtime.InteropServices;

namespace AnzExplorer.App;

/// <summary>
/// Gets the icon File Explorer shows for a Shell item (IShellItemImageFactory). Unlike
/// StorageFolder thumbnails, this honors folder shortcuts, so a network location shows
/// the network-folder icon.
/// </summary>
internal static class ShellItemImage
{
    /// <summary>Returns premultiplied BGRA pixels, top row first, or null.</summary>
    public static (byte[] Pixels, int Width, int Height)? GetIconPixels(string path, int size)
    {
        var factoryId = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref factoryId, out var factory) != 0 || factory is null)
        {
            return null;
        }

        var bitmap = IntPtr.Zero;
        try
        {
            if (factory.GetImage(new NativeSize(size, size), ImageFlags.IconOnly | ImageFlags.BiggerSizeOk, out bitmap) != 0
                || bitmap == IntPtr.Zero)
            {
                return null;
            }

            if (GetObject(bitmap, Marshal.SizeOf<NativeBitmap>(), out var info) == 0
                || info.Width <= 0 || info.Height <= 0)
            {
                return null;
            }

            var header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = info.Width,
                Height = -info.Height, // top-down rows
                Planes = 1,
                BitCount = 32
            };
            var pixels = new byte[info.Width * info.Height * 4];
            var screen = GetDC(IntPtr.Zero);
            try
            {
                if (GetDIBits(screen, bitmap, 0, (uint)info.Height, pixels, ref header, 0) == 0) return null;
            }
            finally
            {
                _ = ReleaseDC(IntPtr.Zero, screen);
            }

            return (pixels, info.Width, info.Height);
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            Marshal.ReleaseComObject(factory);
        }
    }

    [Flags]
    private enum ImageFlags
    {
        BiggerSizeOk = 0x1,
        IconOnly = 0x4
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeSize(int Width, int Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, ImageFlags flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path,
        IntPtr bindContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? factory);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr handle, int size, out NativeBitmap bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);
}
