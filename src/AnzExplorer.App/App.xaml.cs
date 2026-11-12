using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AnzExplorer.Contracts;

namespace AnzExplorer.App;

public partial class App : Application
{
    private readonly List<MainWindow> _windows = [];
    internal Core.IndexerClient Indexer { get; } = new();

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var requestedPath = Environment.GetCommandLineArgs()
            .Skip(1)
            .FirstOrDefault(path => path == SpecialLocations.ThisPc || Directory.Exists(path));
        CreateWindow(requestedPath);
    }

    internal MainWindow CreateWindow(
        string? requestedPath = null,
        bool startEmpty = false,
        bool activate = true)
    {
        var window = new MainWindow(requestedPath, startEmpty);
        _windows.Add(window);
        window.Closed += Window_Closed;
        if (activate) window.Activate();
        return window;
    }

    private async void Window_Closed(object sender, WindowEventArgs args)
    {
        if (sender is MainWindow window) _windows.Remove(window);
        if (_windows.Count == 0)
        {
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

    /// <summary>
    /// Folders whose contents changed through a drop. Visible tabs reload now; hidden
    /// tabs reload when selected instead of showing their cached, stale listing.
    /// </summary>
    internal void NotifyFoldersChanged(IReadOnlySet<string> folders, MainWindow source)
    {
        foreach (var window in _windows)
        {
            window.InvalidateFolders(folders, refreshActiveTab: !ReferenceEquals(window, source));
        }
    }

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
        try
        {
            var paths = new Core.AppPaths();
            paths.EnsureCreated();
            var logPath = Path.Combine(paths.LogDirectory, $"crash-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(logPath, args.Exception.ToString());
        }
        catch (Exception)
        {
            // Crash logging must never cause a second failure.
        }
    }
}
