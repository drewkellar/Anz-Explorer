using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AnzExplorer.Core;

internal readonly record struct NativeFileEntry(
    string Name,
    long Length,
    FileAttributes Attributes);

internal static class NativeDirectoryEnumerator
{
    private const int FindExInfoBasic = 1;
    private const int FindExSearchNameMatch = 0;
    private const int FindFirstExLargeFetch = 2;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorNoMoreFiles = 18;
    private const int ErrorInvalidParameter = 87;

    public static IEnumerable<NativeFileEntry> Enumerate(string directory)
    {
        var searchPath = Path.Combine(ToExtendedPath(directory), "*");
        var handle = FindFirstFileExW(
            searchPath,
            FindExInfoBasic,
            out var data,
            FindExSearchNameMatch,
            0,
            FindFirstExLargeFetch);

        if (handle.IsInvalid && Marshal.GetLastWin32Error() == ErrorInvalidParameter)
        {
            handle.Dispose();
            handle = FindFirstFileExW(
                searchPath,
                FindExInfoBasic,
                out data,
                FindExSearchNameMatch,
                0,
                0);
        }

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is ErrorFileNotFound or ErrorPathNotFound) yield break;
            throw new Win32Exception(error);
        }

        using (handle)
        {
            while (true)
            {
                if (data.FileName is not "." and not "..")
                {
                    var length = ((long)data.FileSizeHigh << 32) | data.FileSizeLow;
                    yield return new NativeFileEntry(data.FileName, length, data.FileAttributes);
                }

                if (FindNextFileW(handle, out data)) continue;
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorNoMoreFiles) yield break;
                throw new Win32Exception(error);
            }
        }
    }

    private static string ToExtendedPath(string path)
    {
        if (path.StartsWith("\\\\?\\", StringComparison.Ordinal)) return path;
        if (path.StartsWith("\\\\", StringComparison.Ordinal)) return "\\\\?\\UNC\\" + path[2..];
        return "\\\\?\\" + path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFindHandle FindFirstFileExW(
        string fileName,
        int infoLevel,
        out Win32FindData findFileData,
        int searchOperation,
        nint searchFilter,
        int additionalFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextFileW(SafeFindHandle findFile, out Win32FindData findFileData);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(nint findFile);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindData
    {
        public FileAttributes FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint Reserved0;
        public uint Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string FileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string AlternateFileName;
    }

    private sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeFindHandle() : base(true) { }
        protected override bool ReleaseHandle() => FindClose(handle);
    }
}

