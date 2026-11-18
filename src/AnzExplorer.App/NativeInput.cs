using System.Runtime.InteropServices;

namespace AnzExplorer.App;

/// <summary>
/// Reads the real pointer and key state from Windows. During a drag-and-drop operation
/// XAML stops receiving pointer input, and over the title-bar area (where the tabs live)
/// it may receive no drag events either, so the drag code asks Windows directly.
/// </summary>
internal static class NativeInput
{
    private const int LeftButton = 0x01;
    private const int ShiftKey = 0x10;
    private const int ControlKey = 0x11;

    /// <summary>The pointer position in the window's client area, in physical pixels.</summary>
    public static bool TryGetCursorClientPosition(IntPtr window, out int x, out int y)
    {
        x = y = 0;
        if (!GetCursorPos(out var point) || !ScreenToClient(window, ref point)) return false;
        x = point.X;
        y = point.Y;
        return true;
    }

    public static bool IsLeftButtonDown => IsDown(LeftButton);
    public static bool IsControlDown => IsDown(ControlKey);
    public static bool IsShiftDown => IsDown(ShiftKey);

    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr window, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}
