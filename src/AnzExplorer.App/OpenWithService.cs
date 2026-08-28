using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace AnzExplorer.App;

internal sealed record OpenWithApplication(string DisplayName, string ExecutablePath);

internal static class OpenWithService
{
    private const int CancelledHResult = unchecked((int)0x800704C7);

    public static IReadOnlyList<OpenWithApplication> GetRecommendedApplications(string path, int maximum = 8)
    {
        var extension = Path.GetExtension(path);
        if (string.IsNullOrWhiteSpace(extension)) return [];

        var candidates = new List<string>();
        AddCandidate(candidates, QueryAssociation(extension, AssociationString.Executable));
        AddRecentApplications(extension, candidates);
        AddRegisteredApplications(extension, candidates);

        var results = new List<OpenWithApplication>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var executable = ResolveExecutable(candidate);
            if (string.IsNullOrWhiteSpace(executable)
                || !seen.Add(executable)
                || Path.GetFileName(executable).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            results.Add(new OpenWithApplication(GetDisplayName(executable), executable));
            if (results.Count >= maximum) break;
        }
        return results;
    }

    public static void Open(OpenWithApplication application, string path)
    {
        var startInfo = new ProcessStartInfo(application.ExecutablePath) { UseShellExecute = true };
        startInfo.ArgumentList.Add(path);
        Process.Start(startInfo);
    }

    public static void ShowChooser(IntPtr ownerWindow, string path)
    {
        var information = new OpenAsInfo
        {
            File = path,
            Class = null,
            Flags = OpenAsFlags.AllowRegistration | OpenAsFlags.Execute
        };
        var result = SHOpenWithDialog(ownerWindow, ref information);
        if (result < 0 && result != CancelledHResult) Marshal.ThrowExceptionForHR(result);
    }

    private static void AddRecentApplications(string extension, ICollection<string> candidates)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{extension}\OpenWithList");
        if (key is null) return;
        var mru = key.GetValue("MRUList") as string ?? string.Empty;
        foreach (var valueName in mru.Select(character => character.ToString()))
        {
            AddCandidate(candidates, key.GetValue(valueName) as string);
        }
        foreach (var valueName in key.GetValueNames().Where(name => name.Length == 1))
        {
            AddCandidate(candidates, key.GetValue(valueName) as string);
        }
    }

    private static void AddRegisteredApplications(string extension, ICollection<string> candidates)
    {
        using (var listKey = Registry.ClassesRoot.OpenSubKey($@"{extension}\OpenWithList"))
        {
            if (listKey is not null)
            {
                foreach (var application in listKey.GetSubKeyNames()) AddCandidate(candidates, application);
            }
        }

        using var progIds = Registry.ClassesRoot.OpenSubKey($@"{extension}\OpenWithProgids");
        if (progIds is null) return;
        foreach (var progId in progIds.GetValueNames())
        {
            using var commandKey = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
            AddCandidate(candidates, ParseExecutable(commandKey?.GetValue(null) as string));
        }
    }

    private static void AddCandidate(ICollection<string> candidates, string? candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate)) candidates.Add(candidate.Trim());
    }

    private static string? ResolveExecutable(string candidate)
    {
        candidate = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));
        if (File.Exists(candidate)) return Path.GetFullPath(candidate);

        var fileName = Path.GetFileName(candidate);
        var associated = QueryAssociation(fileName, AssociationString.Executable, AssociationFlags.InitByExeName);
        if (!string.IsNullOrWhiteSpace(associated) && File.Exists(associated)) return associated;

        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{fileName}");
            var registeredPath = Environment.ExpandEnvironmentVariables(key?.GetValue(null) as string ?? string.Empty);
            if (File.Exists(registeredPath)) return registeredPath;
        }

        using (var commandKey = Registry.ClassesRoot.OpenSubKey($@"Applications\{fileName}\shell\open\command"))
        {
            var commandPath = ParseExecutable(commandKey?.GetValue(null) as string);
            if (!string.IsNullOrWhiteSpace(commandPath) && File.Exists(commandPath)) return commandPath;
        }

        var systemCandidate = Path.Combine(Environment.SystemDirectory, fileName);
        if (File.Exists(systemCandidate)) return systemCandidate;
        var windowsCandidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), fileName);
        return File.Exists(windowsCandidate) ? windowsCandidate : null;
    }

    private static string GetDisplayName(string executable)
    {
        try
        {
            var description = FileVersionInfo.GetVersionInfo(executable).FileDescription;
            if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
        }
        catch (Exception)
        {
            // A readable executable can still have malformed or inaccessible version data.
        }
        return Path.GetFileNameWithoutExtension(executable);
    }

    private static string? ParseExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        if (command.StartsWith('"'))
        {
            var closingQuote = command.IndexOf('"', 1);
            return closingQuote > 1 ? command[1..closingQuote] : null;
        }

        var executableEnd = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return executableEnd >= 0 ? command[..(executableEnd + 4)].Trim() : command.Split(' ', 2)[0];
    }

    private static string? QueryAssociation(
        string association,
        AssociationString value,
        AssociationFlags flags = AssociationFlags.None)
    {
        uint length = 0;
        _ = AssocQueryString(flags, value, association, null, null, ref length);
        if (length == 0) return null;
        var buffer = new StringBuilder((int)length);
        return AssocQueryString(flags, value, association, null, buffer, ref length) == 0
            ? buffer.ToString()
            : null;
    }

    [Flags]
    private enum AssociationFlags : uint
    {
        None = 0,
        InitByExeName = 0x00000002
    }

    private enum AssociationString : uint
    {
        Executable = 2
    }

    [Flags]
    private enum OpenAsFlags : uint
    {
        AllowRegistration = 0x00000001,
        Execute = 0x00000004
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenAsInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public OpenAsFlags Flags;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(
        AssociationFlags flags,
        AssociationString value,
        string association,
        string? extra,
        StringBuilder? output,
        ref uint outputLength);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHOpenWithDialog(IntPtr ownerWindow, ref OpenAsInfo information);
}
