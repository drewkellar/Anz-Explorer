using System.Runtime.InteropServices;

namespace AnzExplorer.App;

internal static class ShellProperties
{
    private const uint ShopFilePath = 0x00000002;

    [DllImport("shell32.dll", EntryPoint = "SHObjectProperties", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowProperties(nint owner, uint type, string name, string? page);

    public static void Show(string path) => ShowProperties(0, ShopFilePath, path, null);
}
