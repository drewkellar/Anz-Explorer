using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnzExplorer.Contracts;

public enum SidebarTargetType
{
    FileSystemPath,
    KnownFolder,
    Drive,
    Network,
    Wsl
}

public enum FolderSizeState
{
    Unknown,
    Queued,
    Scanning,
    Complete,
    Partial,
    Unavailable
}

public enum FileViewMode
{
    Details,
    List,
    Icons,
    ExtraLargeIcons,
    LargeIcons,
    MediumIcons,
    SmallIcons,
    Tiles,
    Content
}

public enum SortDirection
{
    Ascending,
    Descending
}

public sealed record SidebarItem(
    Guid Id,
    string Label,
    string Target,
    SidebarTargetType TargetType,
    string? Icon,
    int Order);

public sealed record SidebarGroup(
    Guid Id,
    string Label,
    int Order,
    bool IsCollapsed,
    IReadOnlyList<SidebarItem> Items);

public sealed record TabSession(
    Guid Id,
    string CurrentPath,
    IReadOnlyList<string> BackHistory,
    IReadOnlyList<string> ForwardHistory,
    FileViewMode ViewMode,
    string SortProperty,
    SortDirection SortDirection,
    string? SelectedPath,
    double ScrollOffset);

public sealed record ExplorerSettings
{
    public const int CurrentSchemaVersion = 4;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public IReadOnlyList<SidebarGroup> SidebarGroups { get; init; } = [];
    public IReadOnlyList<TabSession> Tabs { get; init; } = [];
    public Guid? ActiveTabId { get; init; }
    public bool RestorePreviousSession { get; init; }
    public bool ConfirmRecycleBinDeletes { get; init; } = true;
    public string? FixedHomePath { get; init; }
    public IReadOnlyList<string> IndexedRoots { get; init; } = [];
    public bool PauseIndexingOnBattery { get; init; } = true;
    public DetailsColumnWidths DetailsColumnWidths { get; init; } = new();
    public double DisplayScale { get; init; } = 1.0;

    /// <summary>
    /// Settings written by a newer build that this build does not understand. Kept and
    /// written back unchanged so running an older build never discards them.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownProperties { get; init; }
}

public sealed record DetailsColumnWidths(
    double Name = 480,
    double Modified = 170,
    double Type = 140,
    double Size = 130);

public sealed record FolderSizeResult(
    string Path,
    long? LogicalBytes,
    FolderSizeState State,
    DateTimeOffset? LastUpdated,
    string? ErrorSummary);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(GetFolderSizeRequest), "get-folder-size")]
[JsonDerivedType(typeof(PauseIndexingRequest), "pause")]
[JsonDerivedType(typeof(ResumeIndexingRequest), "resume")]
[JsonDerivedType(typeof(ShutdownRequest), "shutdown")]
public abstract record IndexRequest(Guid RequestId);

public sealed record GetFolderSizeRequest(Guid RequestId, string Path, bool HighPriority = true)
    : IndexRequest(RequestId);

public sealed record PauseIndexingRequest(Guid RequestId) : IndexRequest(RequestId);

public sealed record ResumeIndexingRequest(Guid RequestId) : IndexRequest(RequestId);

public sealed record ShutdownRequest(Guid RequestId) : IndexRequest(RequestId);

public sealed record IndexResponse(Guid RequestId, bool Success, FolderSizeResult? Result, string? Error);

public static class IndexProtocol
{
    public const int Version = 1;
    public static string PipeName => $"AnzExplorer.Indexer.v{Version}.{Environment.UserName}";
}
