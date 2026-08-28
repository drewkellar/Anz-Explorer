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
    DriveType DriveType);

internal static class DriveService
{
    public static IReadOnlyList<DriveDescriptor> GetAvailableDrives()
    {
        var drives = new List<DriveDescriptor>();
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
                drives.Add(new DriveDescriptor(
                    CreateStableId(drive.Name),
                    drive.Name,
                    $"{volumeLabel} ({driveName})",
                    typeLabel,
                    capacity,
                    icon,
                    isReady,
                    drive.DriveType));
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

    public static IReadOnlyList<FileSystemItemViewModel> ToViewModels(IEnumerable<DriveDescriptor> drives) =>
        drives.Select(drive => new FileSystemItemViewModel
        {
            Name = drive.DisplayName,
            Path = drive.RootPath,
            IsFolder = true,
            IsDrive = true,
            Modified = DateTimeOffset.MinValue,
            Type = drive.TypeLabel,
            SizeDescription = drive.CapacityText,
            SizeState = drive.IsReady ? FolderSizeState.Complete : FolderSizeState.Unavailable
        }).ToArray();

    private static Guid CreateStableId(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
        return new Guid(hash.AsSpan(0, 16));
    }
}
