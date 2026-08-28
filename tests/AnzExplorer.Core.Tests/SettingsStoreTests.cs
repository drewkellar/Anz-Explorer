using AnzExplorer.Contracts;
using AnzExplorer.Core;

namespace AnzExplorer.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AnzExplorerTests", Guid.NewGuid().ToString("N"));

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

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
