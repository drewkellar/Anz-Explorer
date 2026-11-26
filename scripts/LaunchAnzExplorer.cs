using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

internal static class LaunchAnzExplorer
{
    private static int Main()
    {
#if Release
        const string Configuration = "Release";
#else
        const string Configuration = "Debug";
#endif
        string root = AppDomain.CurrentDomain.BaseDirectory;
        string appPath = Path.Combine(root, "src", "AnzExplorer.App",
            @"bin\x64\" + Configuration + @"\net10.0-windows10.0.26100.0\win-x64\AnzExplorer.exe");
        try
        {
            Process.Start(new ProcessStartInfo(appPath, GetArguments())
            {
                UseShellExecute = false
            });
            return 0;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            MessageBox(IntPtr.Zero,
                "Run scripts\\build.ps1 to rebuild Anz Explorer.\n\n" + exception.Message,
                "Anz Explorer", 0x10);
            return 1;
        }
    }

    private static string GetArguments()
    {
        // Preserve Windows quoting for forwarded folder paths.
        string commandLine = Environment.CommandLine;
        int executableEnd = commandLine.StartsWith("\"", StringComparison.Ordinal)
            ? commandLine.IndexOf('"', 1) + 1
            : commandLine.IndexOfAny(new[] { ' ', '\t' });
        return executableEnd > 0 ? commandLine.Substring(executableEnd).TrimStart() : string.Empty;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
}
