using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace AnzExplorer.App;

internal static class ShellLinkService
{
    public static void Create(string shortcutPath, string targetPath)
    {
        object? comObject = null;
        try
        {
            comObject = new ShellLinkComObject();
            var shellLink = (IShellLinkW)comObject;
            Marshal.ThrowExceptionForHR(shellLink.SetPath(targetPath));

            var workingDirectory = Directory.Exists(targetPath)
                ? targetPath
                : Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                Marshal.ThrowExceptionForHR(shellLink.SetWorkingDirectory(workingDirectory));
            }

            ((IPersistFile)comObject).Save(shortcutPath, true);
        }
        finally
        {
            if (comObject is not null && Marshal.IsComObject(comObject))
            {
                Marshal.FinalReleaseComObject(comObject);
            }
        }
    }

    /// <summary>
    /// Reads a shortcut's target path without resolving it, so an offline network
    /// target never blocks the caller. Returns null when the target is not a path.
    /// </summary>
    public static string? TryGetTargetPath(string shortcutPath)
    {
        object? comObject = null;
        try
        {
            comObject = new ShellLinkComObject();
            ((IPersistFile)comObject).Load(shortcutPath, 0);
            var shellLink = (IShellLinkW)comObject;
            var buffer = new StringBuilder(32768);
            const uint UncPriority = 0x2;
            if (shellLink.GetPath(buffer, buffer.Capacity, IntPtr.Zero, UncPriority) == 0 && buffer.Length > 0)
            {
                return buffer.ToString();
            }

            if (shellLink.GetIDList(out var itemIdList) == 0 && itemIdList != IntPtr.Zero)
            {
                try
                {
                    buffer.Clear();
                    buffer.EnsureCapacity(32768);
                    if (SHGetPathFromIDListEx(itemIdList, buffer, buffer.Capacity, 0) && buffer.Length > 0)
                    {
                        return buffer.ToString();
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(itemIdList);
                }
            }

            return null;
        }
        catch (Exception exception) when (exception is COMException or IOException or UnauthorizedAccessException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            if (comObject is not null && Marshal.IsComObject(comObject))
            {
                Marshal.FinalReleaseComObject(comObject);
            }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDListEx(IntPtr itemIdList, StringBuilder path, int pathLength, uint options);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLinkComObject
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
        [PreserveSig] int GetIDList(out IntPtr itemIdList);
        [PreserveSig] int SetIDList(IntPtr itemIdList);
        [PreserveSig] int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int maxName);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        [PreserveSig] int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxPath);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        [PreserveSig] int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxPath);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        [PreserveSig] int GetHotkey(out short hotkey);
        [PreserveSig] int SetHotkey(short hotkey);
        [PreserveSig] int GetShowCmd(out int showCommand);
        [PreserveSig] int SetShowCmd(int showCommand);
        [PreserveSig] int GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int iconIndex);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        [PreserveSig] int Resolve(IntPtr windowHandle, uint flags);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
