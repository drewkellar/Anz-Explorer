using AnzExplorer.Contracts;
using AnzExplorer.Core;

namespace AnzExplorer.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AnzExplorerTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FolderSorting_PersistsWithoutSessionRestoration()
    {
        var store = new AtomicJsonSettingsStore(new AppPaths(_root));
        await store.SaveAsync(new ExplorerSettings
        {
            RestorePreviousSession = false,
            FolderSortPreferences = [
                new(@"C:\Users\Test\Downloads", "Modified", SortDirection.Descending),
                new(@"C:\Users\Test\Documents", "Name", SortDirection.Ascending)]
        });
        var loaded = await new AtomicJsonSettingsStore(new AppPaths(_root)).LoadAsync();
        Assert.False(loaded.RestorePreviousSession);
        Assert.Empty(loaded.Tabs);
        Assert.Equal("Modified", loaded.FolderSortPreferences[0].Property);
        Assert.Equal(SortDirection.Descending, loaded.FolderSortPreferences[0].Direction);
        Assert.Equal(SortDirection.Ascending, loaded.FolderSortPreferences[1].Direction);
    }

    [Fact]
    public async Task SaveAndLoad_PreservesExplicitSidebarOrder()
    {
        var paths = new AppPaths(_root);
        var store = new AtomicJsonSettingsStore(paths);
        var first = new SidebarItem(Guid.NewGuid(), "Zeta", @"C:\Zeta", SidebarTargetType.FileSystemPath, null, 0);
        var second = new SidebarItem(Guid.NewGuid(), "Alpha", @"C:\Alpha", SidebarTargetType.FileSystemPath, null, 1);
        var settings = new ExplorerSettings
        {
            SidebarGroups = [new SidebarGroup(Guid.NewGuid(), "Pinned", 0, false, [first, second])]
        };

        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();

        Assert.Equal([first.Id, second.Id], loaded.SidebarGroups[0].Items.Select(item => item.Id));
        Assert.True(File.Exists(paths.SettingsBackupPath));
    }

    [Fact]
    public async Task Load_AcceptsNewerSchemaAndSavePreservesUnknownSettings()
    {
        // Written by a newer build that added a setting this build does not know about.
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.SettingsPath, $$"""
            {
              "schemaVersion": {{ExplorerSettings.CurrentSchemaVersion + 1}},
              "fixedHomePath": "C:\\Known",
              "futureSetting": { "enabled": true, "mode": "experimental" }
            }
            """);
        var store = new AtomicJsonSettingsStore(paths);

        var loaded = await store.LoadAsync();
        Assert.Equal(@"C:\Known", loaded.FixedHomePath);

        await store.SaveAsync(loaded with { DisplayScale = 1.25 });
        var saved = await File.ReadAllTextAsync(paths.SettingsPath);
        Assert.Contains("futureSetting", saved);
        Assert.Contains($"\"schemaVersion\": {ExplorerSettings.CurrentSchemaVersion + 1}", saved);
        Assert.Equal(1.25, (await store.LoadAsync()).DisplayScale);
    }

    [Fact]
    public async Task Load_UsesBackupWhenPrimaryJsonIsCorrupt()
    {
        var paths = new AppPaths(_root);
        var store = new AtomicJsonSettingsStore(paths);
        var settings = new ExplorerSettings { FixedHomePath = @"C:\Known" };
        await store.SaveAsync(settings);
        await File.WriteAllTextAsync(paths.SettingsPath, "not-json");

        var loaded = await store.LoadAsync();

        Assert.Equal(@"C:\Known", loaded.FixedHomePath);
    }

    [Fact]
    public async Task SaveAndLoad_PreservesDetailsColumnWidths()
    {
        var paths = new AppPaths(_root);
        var store = new AtomicJsonSettingsStore(paths);
        var widths = new DetailsColumnWidths(612, 205, 156, 118);

        await store.SaveAsync(new ExplorerSettings { DetailsColumnWidths = widths });
        var loaded = await store.LoadAsync();

        Assert.Equal(widths, loaded.DetailsColumnWidths);
    }

    [Fact]
    public async Task SaveAndLoad_PreservesDisplayScale()
    {
        var paths = new AppPaths(_root);
        var store = new AtomicJsonSettingsStore(paths);

        await store.SaveAsync(new ExplorerSettings { DisplayScale = 1.3 });
        var loaded = await store.LoadAsync();

        Assert.Equal(1.3, loaded.DisplayScale);
    }

    [Theory]
    [InlineData(FileViewMode.ExtraLargeIcons)]
    [InlineData(FileViewMode.LargeIcons)]
    [InlineData(FileViewMode.MediumIcons)]
    [InlineData(FileViewMode.SmallIcons)]
    [InlineData(FileViewMode.List)]
    [InlineData(FileViewMode.Details)]
    [InlineData(FileViewMode.Tiles)]
    [InlineData(FileViewMode.Content)]
    public async Task SaveAndLoad_PreservesTabViewMode(FileViewMode viewMode)
    {
        var paths = new AppPaths(_root);
        var store = new AtomicJsonSettingsStore(paths);
        var tab = new TabSession(
            Guid.NewGuid(), @"C:\", [], [], viewMode, "Name", SortDirection.Ascending, null, 0);

        await store.SaveAsync(new ExplorerSettings { Tabs = [tab] });
        var loaded = await store.LoadAsync();

        Assert.Equal(viewMode, loaded.Tabs.Single().ViewMode);
    }

    [Fact]
    public async Task Load_MigratesV1SessionRestoreToOffByDefault()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.SettingsPath,
            """
            {
              "schemaVersion": 1,
              "restorePreviousSession": true,
              "sidebarGroups": [],
              "tabs": []
            }
            """);

        var loaded = await new AtomicJsonSettingsStore(paths).LoadAsync();

        Assert.Equal(ExplorerSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.False(loaded.RestorePreviousSession);
    }

    [Fact]
    public async Task SaveAndLoad_PreservesExplicitSessionRestoreOptIn()
    {
        var paths = new AppPaths(_root);
        var store = new AtomicJsonSettingsStore(paths);
        await store.SaveAsync(new ExplorerSettings { RestorePreviousSession = true });

        var loaded = await store.LoadAsync();

        Assert.True(loaded.RestorePreviousSession);
    }

    [Fact]
    public async Task SaveAndLoad_PreservesRecycleBinConfirmationPreference()
    {
        var paths = new AppPaths(_root);
        var store = new AtomicJsonSettingsStore(paths);
        await store.SaveAsync(new ExplorerSettings { ConfirmRecycleBinDeletes = false });

        var loaded = await store.LoadAsync();

        Assert.False(loaded.ConfirmRecycleBinDeletes);
    }

    [Fact]
    public async Task Load_MigratesV2WithRecycleBinConfirmationEnabled()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.SettingsPath,
            """
            {
              "schemaVersion": 2,
              "restorePreviousSession": false,
              "sidebarGroups": [],
              "tabs": []
            }
            """);

        var loaded = await new AtomicJsonSettingsStore(paths).LoadAsync();

        Assert.Equal(ExplorerSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.True(loaded.ConfirmRecycleBinDeletes);
    }

    [Fact]
    public async Task Load_MigratesV3WithDefaultDisplayScale()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.SettingsPath,
            """
            {
              "schemaVersion": 3,
              "restorePreviousSession": false,
              "confirmRecycleBinDeletes": true,
              "sidebarGroups": [],
              "tabs": []
            }
            """);

        var loaded = await new AtomicJsonSettingsStore(paths).LoadAsync();

        Assert.Equal(ExplorerSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(1.0, loaded.DisplayScale);
    }

    [Fact]
    public async Task ConcurrentStores_DoNotCollideOnTheAtomicTemporaryFile()
    {
        var paths = new AppPaths(_root);
        var stores = Enumerable.Range(0, 4).Select(_ => new AtomicJsonSettingsStore(paths)).ToArray();

        await Task.WhenAll(Enumerable.Range(0, 24).Select(index =>
            stores[index % stores.Length].SaveAsync(new ExplorerSettings
            {
                FixedHomePath = $@"C:\Home-{index}"
            })));

        var loaded = await stores[0].LoadAsync();
        Assert.StartsWith(@"C:\Home-", loaded.FixedHomePath);
        Assert.True(File.Exists(paths.SettingsBackupPath));
    }

    [Fact]
    public async Task Load_FallsBackWhenPrimaryHasDuplicateIds_AndNextSaveKeepsAGoodBackup()
    {
        var paths = new AppPaths(_root);
        var store = new AtomicJsonSettingsStore(paths);
        await store.SaveAsync(new ExplorerSettings { FixedHomePath = @"C:\Known" });
        await store.SaveAsync(new ExplorerSettings { FixedHomePath = @"C:\Known" });
        var id = Guid.NewGuid();
        await File.WriteAllTextAsync(paths.SettingsPath, $$"""
            {"schemaVersion":5,"fixedHomePath":"C:\\Bad","sidebarGroups":[
              {"id":"{{id}}","label":"Pinned","order":0,"isCollapsed":false,"items":[
                {"id":"{{id}}","label":"Dup","target":"C:\\Dup","targetType":"fileSystemPath","icon":null,"order":0}]}]}
            """);

        var loaded = await store.LoadAsync();
        Assert.Equal(@"C:\Known", loaded.FixedHomePath);
        Assert.Single(Directory.GetFiles(_root, "settings.unreadable-*.json"));

        // Previously the bad primary was rotated into the backup slot here, destroying it.
        await store.SaveAsync(loaded with { FixedHomePath = @"C:\New" });
        await File.WriteAllTextAsync(paths.SettingsPath, "not-json");
        Assert.Equal(@"C:\New", (await store.LoadAsync()).FixedHomePath);
    }

    [Fact]
    public async Task Load_TreatsNullCollectionsAsCorruptAndUsesBackup()
    {
        var paths = new AppPaths(_root);
        var store = new AtomicJsonSettingsStore(paths);
        await store.SaveAsync(new ExplorerSettings { FixedHomePath = @"C:\Known" });
        await File.WriteAllTextAsync(paths.SettingsPath, """{"schemaVersion":5,"sidebarGroups":null}""");

        var loaded = await store.LoadAsync();

        Assert.Equal(@"C:\Known", loaded.FixedHomePath);
        Assert.NotNull(loaded.SidebarGroups);
    }

    [Fact]
    public async Task Load_UsesNewerSchemaFileInsteadOfQuarantiningIt()
    {
        // Newer settings are read and written back (with unknown fields preserved) rather
        // than set aside, so switching between builds keeps the user's settings.
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.SettingsPath, """{"schemaVersion":99,"fixedHomePath":"C:\\Future"}""");

        var loaded = await new AtomicJsonSettingsStore(paths).LoadAsync();
        await new AtomicJsonSettingsStore(paths).SaveAsync(loaded);

        Assert.Equal(@"C:\Future", loaded.FixedHomePath);
        Assert.Empty(Directory.GetFiles(_root, "settings.unreadable-*.json"));
        Assert.Equal(99, (await new AtomicJsonSettingsStore(paths).LoadAsync()).SchemaVersion);
    }

    [Fact]
    public async Task Load_ThrowsInsteadOfReturningDefaultsWhenFilesAreLocked()
    {
        var paths = new AppPaths(_root);
        var store = new AtomicJsonSettingsStore(paths);
        await store.SaveAsync(new ExplorerSettings { FixedHomePath = @"C:\Known" });
        using var primaryLock = new FileStream(paths.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None);
        using var backupLock = new FileStream(paths.SettingsBackupPath, FileMode.Open, FileAccess.Read, FileShare.None);

        await Assert.ThrowsAsync<IOException>(() => store.LoadAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
