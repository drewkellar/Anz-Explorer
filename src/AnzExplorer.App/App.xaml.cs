using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AnzExplorer.Contracts;

namespace AnzExplorer.App;

public partial class App : Application
{
    private readonly List<MainWindow> _windows = [];
    internal Core.IndexerClient Indexer { get; } = new();
    internal Dictionary<string, FolderSortPreference> FolderSortPreferences { get; } = new(StringComparer.OrdinalIgnoreCase);
    // One sidebar for every window. Seeded by the first window that loads settings successfully.
    internal List<SidebarGroup> SidebarGroups { get; } = [];
    internal bool SidebarLoaded { get; private set; }

    internal void SeedSidebar(IEnumerable<SidebarGroup> groups)
    {
        if (SidebarLoaded) return;
        SidebarGroups.Clear();
        SidebarGroups.AddRange(groups.OrderBy(group => group.Order));
        SidebarLoaded = true;
    }

    internal void NotifySidebarChanged(MainWindow source)
    {
        foreach (var window in _windows)
        {
            if (!ReferenceEquals(window, source)) window.OnSharedSidebarChanged();
        }
    }

    public App()
    {
        // Start logging before anything else so even startup failures leave a trail.
        try
        {
            var paths = new Core.AppPaths();
            paths.EnsureCreated();
            Core.DiagnosticLog.Initialize(paths.LogDirectory, "app");
            Core.DiagnosticLog.HookProcessExceptions();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The app must still run when its log folder is unavailable.
        }

        InitializeComponent();
        UnhandledException += OnUnhandledException;
        DebugSettings.BindingFailed += (_, args) =>
            Core.DiagnosticLog.Warn("Binding", args.Message);
        DebugSettings.XamlResourceReferenceFailed += (_, args) =>
            Core.DiagnosticLog.Warn("XamlResource", args.Message);
    }

    internal static string? LogDirectory => Core.DiagnosticLog.Current?.LogDirectory;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var requestedPath = Environment.GetCommandLineArgs()
            .Skip(1)
            .FirstOrDefault(path => path == SpecialLocations.ThisPc || Directory.Exists(path));
        Core.DiagnosticLog.Info("App", $"Launched; requested={Core.DiagnosticLogger.DescribePath(requestedPath)}");
        CreateWindow(requestedPath);
    }

    internal MainWindow CreateWindow(
        string? requestedPath = null,
        bool startEmpty = false,
        bool activate = true,
        MainWindow? tearOutSource = null)
    {
        var window = new MainWindow(requestedPath, startEmpty, tearOutSource);
        _windows.Add(window);
        Core.DiagnosticLog.Info("Window", $"Created W{window.DiagnosticId} (open={_windows.Count}, startEmpty={startEmpty}, tearOut={tearOutSource is not null})");
        window.Closed += Window_Closed;
        if (activate) window.Activate();
        return window;
    }

    private async void Window_Closed(object sender, WindowEventArgs args)
    {
        if (sender is MainWindow window)
        {
            _windows.Remove(window);
            Core.DiagnosticLog.Info("Window", $"Closed W{window.DiagnosticId} (open={_windows.Count})");
        }
        if (_windows.Count == 0)
        {
            // Mark a clean end before awaiting: the process can exit as soon as the last
            // window closes, and a missing marker would be reported as a crash next launch.
            Core.DiagnosticLog.Current?.EndSession();
            await Indexer.DisposeAsync();
        }
    }

    internal MainWindow? FindWindowContaining(TabViewItem tab) =>
        _windows.FirstOrDefault(window => window.ContainsTab(tab));

    internal MainWindow? FindWindowFor(TabView tabView) =>
        _windows.FirstOrDefault(window => window.OwnsTabView(tabView));

    internal MainWindow? FindWindowAtTabStrip(
        Windows.Graphics.PointInt32 screenPoint,
        MainWindow source) =>
        _windows.LastOrDefault(window =>
            !ReferenceEquals(window, source) && window.ContainsScreenPointInTabStrip(screenPoint))
        ?? (source.ContainsScreenPointInTabStrip(screenPoint) ? source : null);

    internal IReadOnlyList<TabSession> GetOpenTabSessions() =>
        _windows.SelectMany(window => window.GetTabSessions())
            .DistinctBy(session => session.Id)
            .ToArray();

    internal void ApplyColumnWidths(DetailsColumnWidths widths)
    {
        foreach (var window in _windows)
        {
            window.ApplyColumnWidths(widths);
        }
    }

    internal void ApplyRestoreTabsPreference(bool restorePreviousSession)
    {
        foreach (var window in _windows)
        {
            window.ApplyRestoreTabsPreference(restorePreviousSession);
        }
    }

    internal void ApplyRecycleBinDeleteConfirmationPreference(bool confirmRecycleBinDeletes)
    {
        foreach (var window in _windows)
        {
            window.ApplyRecycleBinDeleteConfirmationPreference(confirmRecycleBinDeletes);
        }
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        // args.Message often carries XAML's own description, which args.Exception lacks.
        Core.DiagnosticLog.Current?.WriteCrashReport(
            $"XAML UnhandledException: {args.Message}", args.Exception);
    }
}
