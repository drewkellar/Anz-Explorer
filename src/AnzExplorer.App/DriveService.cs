using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AnzExplorer.Contracts;

namespace AnzExplorer.App;

internal static class SpecialLocations
{
    public const string ThisPc = "anz://this-pc";
    public static readonly Guid ThisPcId = Guid.Parse("9b4b3ca1-7349-41fc-bef0-6ea23ad6a39a");
}

internal sealed record DriveDescriptor(
    Guid Id,
    string RootPath,
    string DisplayName,
    string TypeLabel,
    string CapacityText,
    string Icon,
    bool IsReady,
    DriveType DriveType,
    long? TotalBytes = null,
    long? FreeBytes = null);

/// <summary>A folder shortcut to a network share, as added with "Add a network location".</summary>
internal sealed record NetworkLocation(string Name, string TargetPath, string ShortcutPath);

internal static class DriveService
{
    public static IReadOnlyList<DriveDescriptor> GetAvailableDrives()
    {
        var drives = new List<DriveDescriptor>();
        var seenIds = new HashSet<Guid>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                var typeLabel = drive.DriveType switch
                {
                    DriveType.Fixed => "Local Disk",
                    DriveType.Removable => "Removable Drive",
                    DriveType.Network => "Network Drive",
                    DriveType.CDRom => "Optical Drive",
                    DriveType.Ram => "RAM Drive",
                    _ => "Drive"
                };
                var icon = drive.DriveType switch
                {
                    DriveType.Network => "Network",
                    DriveType.Removable => "Removable",
                    _ => "Drive"
                };
                var isReady = drive.IsReady;
                var volumeLabel = isReady && !string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? drive.VolumeLabel
                    : typeLabel;
                var driveName = drive.Name.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                var capacity = isReady
                    ? $"{FileSystemItemViewModel.FormatBytes(drive.AvailableFreeSpace)} free of {FileSystemItemViewModel.FormatBytes(drive.TotalSize)}"
                    : "Not ready";
                var id = GetDriveId(drive);
                if (!seenIds.Add(id)) id = CreateStableId(drive.Name);
                drives.Add(new DriveDescriptor(
                    id,
                    drive.Name,
                    $"{volumeLabel} ({driveName})",
                    typeLabel,
                    capacity,
                    icon,
                    isReady,
                    drive.DriveType,
                    isReady ? drive.TotalSize : null,
                    isReady ? drive.AvailableFreeSpace : null));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A drive can disappear between GetDrives and property access.
            }
        }

        return drives
            .OrderBy(drive => drive.DriveType == DriveType.Network)
            .ThenBy(drive => drive.RootPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public const string DevicesGroup = "Devices and drives";
    public const string NetworkGroup = "Network locations";

    /// <summary>
    /// Builds the This PC items in display order: local devices first, then mapped
    /// network drives and network locations (as File Explorer groups them).
    /// </summary>
    public static IReadOnlyList<FileSystemItemViewModel> ToViewModels(
        IEnumerable<DriveDescriptor> drives,
        IEnumerable<NetworkLocation>? networkLocations = null)
    {
        var items = drives.Select(drive => new FileSystemItemViewModel
        {
            Name = drive.DisplayName,
            Path = drive.RootPath,
            IsFolder = true,
            IsDrive = true,
            IsNetworkLocation = drive.DriveType == DriveType.Network,
            GroupName = drive.DriveType == DriveType.Network ? NetworkGroup : DevicesGroup,
            UsedFraction = drive is { TotalBytes: > 0, FreeBytes: { } free }
                ? Math.Clamp(1 - (double)free / drive.TotalBytes.Value, 0, 1)
                : null,
            Modified = DateTimeOffset.MinValue,
            Type = drive.TypeLabel,
            SizeDescription = drive.CapacityText,
            SizeState = drive.IsReady ? FolderSizeState.Complete : FolderSizeState.Unavailable
        }).ToList();

        items.AddRange((networkLocations ?? []).Select(location => new FileSystemItemViewModel
        {
            Name = location.Name,
            Path = location.TargetPath,
            IconSourcePath = location.ShortcutPath,
            IsFolder = true,
            IsDrive = true,
            IsNetworkLocation = true,
            GroupName = NetworkGroup,
            Modified = DateTimeOffset.MinValue,
            Type = "Network location",
            SizeState = FolderSizeState.Complete
        }));

        return items
            .OrderBy(item => item.GroupName == NetworkGroup)
            .ThenBy(item => item.IconSourcePath is not null)
            // Drives in letter order (as File Explorer lists them), network locations by name.
            .ThenBy(item => item.IconSourcePath is null ? item.Path : item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static Guid GetDriveId(DriveInfo drive)
    {
        if (drive.DriveType is not DriveType.Network)
        {
            var volumeName = new StringBuilder(50);
            if (GetVolumeNameForVolumeMountPoint(drive.Name, volumeName, (uint)volumeName.Capacity))
            {
                return CreateStableId(volumeName.ToString());
            }
        }

        return CreateStableId(drive.Name);
    }

    private static Guid CreateStableId(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
        return new Guid(hash.AsSpan(0, 16));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "GetVolumeNameForVolumeMountPointW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, uint bufferLength);
}

internal static class NetworkLocationService
{
    /// <summary>
    /// Lists the user's network locations from the Network Shortcuts folder. Only local
    /// shortcut files are read; the network targets are never contacted.
    /// </summary>
    public static IReadOnlyList<NetworkLocation> GetNetworkLocations()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.NetworkShortcuts);
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return [];

        var locations = new List<NetworkLocation>();
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(root))
            {
                // "Add a network location" creates a folder holding target.lnk; a plain
                // .lnk dropped into the folder is shown by File Explorer too.
                var isFolder = Directory.Exists(entry);
                var shortcut = isFolder ? Path.Combine(entry, "target.lnk") : entry;
                if (!shortcut.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || !File.Exists(shortcut)) continue;

                var target = ShellLinkService.TryGetTargetPath(shortcut);
                if (string.IsNullOrWhiteSpace(target) || !Path.IsPathFullyQualified(target)) continue;

                var name = isFolder ? Path.GetFileName(entry) : Path.GetFileNameWithoutExtension(entry);
                locations.Add(new NetworkLocation(name, target, entry));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An unreadable shortcuts folder only means there are no network locations to show.
        }

        return locations;
    }
}
