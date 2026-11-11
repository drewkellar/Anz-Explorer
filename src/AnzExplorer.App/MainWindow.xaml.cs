using System.Collections.ObjectModel;
using System.Diagnostics;
using AnzExplorer.Contracts;
using AnzExplorer.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;
using DataTransferDragDropModifiers = Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers;

namespace AnzExplorer.App;

public sealed partial class MainWindow : Window
{
    private sealed record OpenWithRequest(OpenWithApplication Application, string Path);

    private enum NewFileKind
    {
        AutoHotkey,
        Bitmap,
        Python,
        Text
    }

    private const string InternalFilePathsFormat = "AnzExplorer.FilePaths";
    private const double TabDragThreshold = 10;
    private const double MinimumTabWidth = 150;
    private const double MaximumTabWidth = 240;
    private const double MinimumDisplayScale = 0.8;
    private const double MaximumDisplayScale = 1.5;
    private const double DisplayScaleStep = 0.1;
    private const VirtualKey OemPlusKey = (VirtualKey)187;
    private const VirtualKey OemMinusKey = (VirtualKey)189;
    private readonly AppPaths _paths = new();
    private readonly ISettingsStore _settingsStore;
    private readonly DirectoryService _directoryService = new();
    private readonly IndexerClient _indexer;
    private readonly FolderSizeScanner _fallbackScanner = new();
    private readonly ShellIconService _shellIcons = new();
    private readonly Windows.UI.ViewManagement.AccessibilitySettings _accessibilitySettings = new();
    private readonly SemaphoreSlim _sizeConcurrency = new(1, 1);
    private readonly ObservableCollection<FileSystemItemViewModel> _emptyItems = [];
    private readonly DispatcherTimer _driveRefreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _dragScrollTimer = new() { Interval = TimeSpan.FromMilliseconds(75) };
    private readonly DispatcherTimer _tabHoverTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly string? _requestedPath;
    private ExplorerSettings _settings = new();
    // Sidebar state is owned by App so every window edits and saves the same list.
    private List<SidebarGroup> _sidebarGroups => ((App)Application.Current).SidebarGroups;
    private CancellationTokenSource? _navigationCancellation;
    private CancellationTokenSource? _searchCancellation;
    private bool _initializing = true;
    // False if settings could not be read; saving defaults then would erase the user's file.
    private bool _settingsLoaded;
    private bool _driveRefreshRunning;
    private IReadOnlyList<DriveDescriptor> _drives = [];
    private IReadOnlyList<FileSystemItemViewModel> _draggedItems = [];
    private ListView? _fileDragSourceList;
    private TabRuntime? _fileDragSourceTab;
    private ScrollViewer? _fileScrollViewer;
    private Windows.Foundation.Point? _dragPosition;
    private ScrollMode _previousVerticalScrollMode = ScrollMode.Enabled;
    private bool _controlledDragScrolling;
    private bool _dropHandledInApp;
    private string? _pendingDropDestination;
    private TabViewItem? _hoveredDropTab;
    private TabViewItem? _pointerDragTab;
    private Windows.Foundation.Point _tabPointerStart;
    private uint _tabPointerId;
    private bool _tabPointerTracking;
    private bool _tabPointerDragging;
    private TabDragPreviewWindow? _tabDragPreview;
    private MainWindow? _tabDropHighlightWindow;
    private readonly bool _startEmpty;
    private bool _suppressStateSaveOnClose;
    private readonly HashSet<FileSystemItemViewModel> _marqueeBaseSelection = [];
    private Windows.Foundation.Point _marqueeStart;
    private uint _marqueePointerId;
    private bool _marqueePending;
    private bool _marqueeActive;
    private bool _marqueeToggle;
    private bool _marqueeExtend;
    private readonly Task _initializationTask;
    private bool _primaryFileListAssigned;
    private bool _addressEditActive;
    private bool _restoringTabSearch;
    private MainWindow? _tearOutSource;
    private static int _nextDiagnosticId;

    /// <summary>Short per-process window number used to tell windows apart in the diagnostic log.</summary>
    internal int DiagnosticId { get; } = Interlocked.Increment(ref _nextDiagnosticId);

    // Breadcrumbs record what the user did, never which file: paths are logged by shape only.
    private void Breadcrumb(string action, string? detail = null) =>
        DiagnosticLog.Info("UI", detail is null ? $"W{DiagnosticId} {action}" : $"W{DiagnosticId} {action}: {detail}");

    private static string Shape(string? path) => DiagnosticLogger.DescribePath(path);

    public MainWindow(string? requestedPath, bool startEmpty = false, MainWindow? tearOutSource = null)
    {
        _tearOutSource = tearOutSource;
        _requestedPath = requestedPath;
        _startEmpty = startEmpty;
        _indexer = ((App)Application.Current).Indexer;
        _settingsStore = new AtomicJsonSettingsStore(_paths);
        InitializeComponent();
        // TabView's internal list can handle drag events before TabViewItem.
        // Route file drags by header bounds after those internal handlers run.
        RootLayout.AddHandler(UIElement.DragOverEvent,
            new DragEventHandler(TabStripFile_DragOver), handledEventsToo: true);
        RootLayout.AddHandler(UIElement.DropEvent,
            new DragEventHandler(TabStripFile_Drop), handledEventsToo: true);
        FileViewHost.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(FileViewHost_PointerPressed),
            handledEventsToo: true);
        RootLayout.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(RootLayout_PointerPressed),
            handledEventsToo: true);
        RootLayout.AddHandler(
            UIElement.PointerMovedEvent,
            new PointerEventHandler(ExplorerTab_PointerMoved),
            handledEventsToo: true);
        RootLayout.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(ExplorerTab_PointerReleased),
            handledEventsToo: true);
        RootLayout.AddHandler(
            UIElement.PointerCanceledEvent,
            new PointerEventHandler(ExplorerTab_PointerCanceled),
            handledEventsToo: true);
        RootLayout.AddHandler(
            UIElement.KeyDownEvent,
            new KeyEventHandler(RootLayout_KeyDown),
            handledEventsToo: true);
        FileViewHost.AddHandler(
            UIElement.PointerMovedEvent,
            new PointerEventHandler(FileViewHost_PointerMoved),
            handledEventsToo: true);
        FileViewHost.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(FileViewHost_PointerReleased),
            handledEventsToo: true);
        FileViewHost.AddHandler(
            UIElement.PointerCanceledEvent,
            new PointerEventHandler(FileViewHost_PointerCanceled),
            handledEventsToo: true);
        ExtendsContentIntoTitleBar = true;
        // Keep the interactive TabView out of the native drag rectangle. If the
        // entire title-bar grid is registered, tiny movements on a tab can invoke
        // the OS window-drag animation before XAML receives the pointer sequence.
        SetTitleBar(TitleBarDragRegion);
        if (_startEmpty)
        {
            AppWindow.IsShownInSwitchers = false;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1, 1));
        }
        else
        {
            SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));
        }
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AnzExplorer.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        Closed += MainWindow_Closed;
        _driveRefreshTimer.Tick += DriveRefreshTimer_Tick;
        _dragScrollTimer.Tick += DragScrollTimer_Tick;
        _tabHoverTimer.Tick += TabHoverTimer_Tick;
        _initializationTask = InitializeAsync();
    }

    private TabRuntime? ActiveTab => (Tabs.SelectedItem as TabViewItem)?.Tag as TabRuntime;
    private ListView FileList => ActiveTab?.FileList ?? PrimaryFileList;
    private ObservableCollection<FileSystemItemViewModel> Items => ActiveTab?.Items ?? _emptyItems;
    private ColumnLayoutState ColumnLayout => (ColumnLayoutState)RootLayout.Resources["ColumnLayout"];
    private DisplayScaleState DisplayScale => (DisplayScaleState)RootLayout.Resources["DisplayScale"];

    private async Task InitializeAsync()
    {
        try
        {
            _settings = _tearOutSource?._settings ?? await _settingsStore.LoadAsync();
            _settingsLoaded = true;
            foreach (var preference in _settings.FolderSortPreferences)
                ((App)Application.Current).FolderSortPreferences.TryAdd(preference.Path, preference);
            ColumnLayout.Apply(_settings.DetailsColumnWidths);
            DisplayScale.Scale = _settings.DisplayScale;
            UpdateZoomCommandState();
            // The first window seeds the shared sidebar; later windows reuse the live
            // in-memory list so they never resurrect a stale copy from disk.
            ((App)Application.Current).SeedSidebar(_settings.SidebarGroups);
            _drives = _tearOutSource?._drives ?? await Task.Run(DriveService.GetAvailableDrives);
            RebuildSidebar();
            _indexer.EnsureWorkerStarted();

            if (_startEmpty)
            {
                // A tab will be transferred into this window by the tear-out workflow.
            }
            else if (!string.IsNullOrWhiteSpace(_requestedPath))
            {
                AddTab(_requestedPath, true);
            }
            else if (_settings.RestorePreviousSession && _settings.Tabs.Count > 0)
            {
                foreach (var session in _settings.Tabs)
                {
                    AddTab(session, false);
                }

                var selected = Tabs.TabItems
                    .OfType<TabViewItem>()
                    .FirstOrDefault(tab => (tab.Tag as TabRuntime)?.Session.Id == _settings.ActiveTabId);
                Tabs.SelectedItem = selected ?? Tabs.TabItems[0];
            }
            else
            {
                AddTab(ResolveHomePath(), true);
            }

            if (ActiveTab is { } activeTab)
            {
                ShowTabView(activeTab);
                if (string.Equals(activeTab.LoadedPath, activeTab.Session.CurrentPath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(activeTab.LoadedSearch, SearchBox.Text, StringComparison.Ordinal))
                {
                    UpdateAddressBar(activeTab.Session.CurrentPath);
                    SynchronizeSidebarSelection(activeTab.Session.CurrentPath);
                    UpdateSortIndicators();
                    UpdateCommandState();
                    UpdateStatusText();
                    RestoreCachedTabViewState(activeTab);
                    RestartIncompleteFolderSizes(activeTab);
                }
                else
                {
                    await NavigateAsync(activeTab.Session.CurrentPath, false);
                }
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("UI", $"W{DiagnosticId} Initialization failed", exception);
            await ShowErrorAsync("Anz Explorer could not restore its previous state.", exception);
            AddTab(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), true);
        }
        finally
        {
            _tearOutSource = null;
            _initializing = false;
            _driveRefreshTimer.Start();
        }
    }

    private string ResolveHomePath() =>
        Directory.Exists(_settings.FixedHomePath)
            ? _settings.FixedHomePath!
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private void RebuildSidebar()
    {
        SidebarNavigation.MenuItems.Clear();

        foreach (var group in _sidebarGroups.OrderBy(group => group.Order))
        {
            SidebarNavigation.MenuItems.Add(new NavigationViewItemHeader { Content = group.Label });
            foreach (var item in group.Items.OrderBy(item => item.Order))
            {
                var navigationItem = CreateSidebarNavigationItem(item, isPinned: true);
                SidebarNavigation.MenuItems.Add(navigationItem);
            }
        }

        if (_sidebarGroups.Any(group => group.Items.Count > 0))
        {
            SidebarNavigation.MenuItems.Add(new NavigationViewItemSeparator());
        }

        SidebarNavigation.MenuItems.Add(new NavigationViewItemHeader { Content = "Devices" });
        var thisPcItem = new SidebarItem(
            SpecialLocations.ThisPcId,
            "This PC",
            SpecialLocations.ThisPc,
            SidebarTargetType.KnownFolder,
            "ThisPc",
            -1);
        var thisPcNavigationItem = CreateSidebarNavigationItem(thisPcItem, isPinned: false);
        SidebarNavigation.MenuItems.Add(thisPcNavigationItem);

        foreach (var drive in _drives)
        {
            var driveItem = new SidebarItem(
                drive.Id,
                drive.DisplayName,
                drive.RootPath,
                drive.DriveType == DriveType.Network ? SidebarTargetType.Network : SidebarTargetType.Drive,
                drive.Icon,
                -1);
            var navigationItem = CreateSidebarNavigationItem(driveItem, isPinned: false);
            if (!drive.IsReady)
            {
                ToolTipService.SetToolTip(navigationItem, "This device is currently unavailable");
            }
            SidebarNavigation.MenuItems.Add(navigationItem);
        }

        SynchronizeSidebarSelection(ActiveTab?.Session.CurrentPath);
    }

    private void SynchronizeSidebarSelection(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            SidebarNavigation.SelectedItem = null;
            return;
        }

        NavigationViewItem? best = null;
        var bestLength = -1;
        foreach (var navigationItem in SidebarNavigation.MenuItems.OfType<NavigationViewItem>())
        {
            if (navigationItem.Tag is not SidebarItem item) continue;
            if (string.Equals(item.Target, path, StringComparison.OrdinalIgnoreCase))
            {
                best = navigationItem;
                break;
            }

            if (item.Target == SpecialLocations.ThisPc || path == SpecialLocations.ThisPc) continue;
            try
            {
                if (PathCanonicalizer.IsSameOrDescendant(path, item.Target)
                    && item.Target.Length > bestLength)
                {
                    best = navigationItem;
                    bestLength = item.Target.Length;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                // An unavailable or non-filesystem sidebar target cannot be selected by ancestry.
            }
        }

        SidebarNavigation.SelectedItem = best;
    }

    private NavigationViewItem CreateSidebarNavigationItem(SidebarItem item, bool isPinned)
    {
        var navigationItem = new NavigationViewItem
        {
            Content = item.Label,
            Tag = item,
            Icon = new FontIcon
            {
                Glyph = ResolveSidebarGlyph(item.Icon),
                Foreground = ResolveSidebarIconBrush(item.Icon)
            },
            ContextFlyout = CreateSidebarContextFlyout(item, isPinned),
            AllowDrop = item.Target != SpecialLocations.ThisPc
        };
        navigationItem.DragOver += SidebarItem_DragOver;
        navigationItem.DragLeave += SidebarItem_DragLeave;
        navigationItem.Drop += SidebarItem_Drop;
        AutomationProperties.SetName(navigationItem, item.Label);
        if (!IsTargetPotentiallyAvailable(item))
        {
            ToolTipService.SetToolTip(navigationItem, "This location is currently unavailable");
        }

        return navigationItem;
    }

    private MenuFlyout CreateSidebarContextFlyout(SidebarItem item, bool isPinned)
    {
        var flyout = new MenuFlyout();
        flyout.Items.Add(CreateSidebarMenuItem("Open", item, SidebarOpen_Click));
        flyout.Items.Add(CreateSidebarMenuItem("Open in new tab", item, SidebarOpenNewTab_Click));
        flyout.Items.Add(CreateSidebarMenuItem("Open in new window", item, SidebarOpenNewWindow_Click));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(CreateSidebarMenuItem("Copy path", item, SidebarCopyPath_Click));
        flyout.Items.Add(CreateSidebarMenuItem("Open in Windows Explorer", item, SidebarOpenInExplorer_Click));
        flyout.Items.Add(CreateSidebarMenuItem("Properties", item, SidebarProperties_Click));
        if (isPinned)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(CreateSidebarMenuItem("Unpin", item, SidebarUnpin_Click));
        }

        return flyout;
    }

    private static MenuFlyoutItem CreateSidebarMenuItem(
        string label,
        SidebarItem item,
        RoutedEventHandler clickHandler)
    {
        var menuItem = new MenuFlyoutItem { Text = label, Tag = item };
        menuItem.Click += clickHandler;
        return menuItem;
    }

    private static string ResolveSidebarGlyph(string? icon) => icon switch
    {
        "Home" => "\uE80F",
        "Desktop" => "\uE7F8",
        "Document" => "\uE8A5",
        "Download" => "\uE896",
        "Drive" => "\uEDA2",
        "Network" => "\uE968",
        "Removable" => "\uE88E",
        "ThisPc" => "\uE7F8",
        _ => "\uE8B7"
    };

    private static Microsoft.UI.Xaml.Media.Brush ResolveSidebarIconBrush(string? icon)
    {
        var key = icon switch
        {
            "Home" or "Desktop" or "Document" or "ThisPc" => "AnzHomeBrush",
            "Download" => "AnzDownloadBrush",
            "Drive" => "AnzDriveBrush",
            "Network" => "AnzNetworkBrush",
            "Removable" => "AnzRemovableBrush",
            _ => "AnzFolderBrush"
        };
        return (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[key];
    }

    private static bool IsTargetPotentiallyAvailable(SidebarItem item) =>
        item.Target == SpecialLocations.ThisPc
        || item.TargetType is SidebarTargetType.Network or SidebarTargetType.Wsl
        || Directory.Exists(item.Target);

    private void AddTab(string path, bool select)
    {
        var session = new TabSession(
            Guid.NewGuid(), path, [], [], FileViewMode.Details, nameof(FileSystemItemViewModel.Name),
            SortDirection.Ascending, null, 0);
        AddTab(session, select);
    }

    private void AddTab(TabSession session, bool select)
    {
        Breadcrumb("Add tab", $"{Shape(session.CurrentPath)} select={select} tabs={Tabs.TabItems.Count + 1}");
        var runtime = new TabRuntime(session, this);
        runtime.FileList = CreateFileList(runtime);
        var item = new TabViewItem
        {
            Header = GetTabTitle(session.CurrentPath),
            IconSource = new FontIconSource { Glyph = "\uE8B7" },
            Tag = runtime,
            IsClosable = true,
            MinWidth = MinimumTabWidth,
            MaxWidth = MaximumTabWidth
        };
        item.Style = (Style)RootLayout.Resources["ExplorerTabStyle"];
        AttachTabDropHandlers(item);
        Tabs.TabItems.Add(item);
        if (select)
        {
            Tabs.SelectedItem = item;
            ShowTabView(runtime);
        }
        UpdateTabSelectionPresentation();
    }

    private ListView CreateFileList(TabRuntime runtime)
    {
        runtime.AppliedViewMode = null;
        if (!_primaryFileListAssigned)
        {
            _primaryFileListAssigned = true;
            PrimaryFileList.ItemsSource = runtime.Items;
            PrimaryFileList.Visibility = Visibility.Collapsed;
            return PrimaryFileList;
        }

        var list = new ListView
        {
            ItemsSource = runtime.Items,
            ItemContainerStyleSelector = PrimaryFileList.ItemContainerStyleSelector,
            ItemTemplate = PrimaryFileList.ItemTemplate,
            IsItemClickEnabled = true,
            CanDragItems = true,
            AllowDrop = true,
            IsSwipeEnabled = false,
            SelectionMode = ListViewSelectionMode.Extended,
            Visibility = Visibility.Collapsed,
            ItemContainerTransitions = null,
            Transitions = null
        };
        Grid.SetRow(list, 1);
        AttachFileListHandlers(list);
        FileViewHost.Children.Insert(Math.Max(0, FileViewHost.Children.Count - 1), list);
        return list;
    }

    private void AttachFileListHandlers(ListView list)
    {
        list.DragOver += FileList_DragOver;
        list.DragLeave += FileList_DragLeave;
        list.Drop += FileList_Drop;
        list.DragItemsStarting += FileList_DragItemsStarting;
        list.DragItemsCompleted += FileList_DragItemsCompleted;
        list.DoubleTapped += FileList_DoubleTapped;
        list.RightTapped += FileList_RightTapped;
        list.SelectionChanged += FileList_SelectionChanged;
        list.KeyDown += FileList_KeyDown;
        list.ContainerContentChanging += FileList_ContainerContentChanging;
    }

    private async void FileList_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.Item is not FileSystemItemViewModel item || item.IsFolder || item.IsDrive)
        {
            return;
        }
        if (args.InRecycleQueue)
        {
            // Image previews are item-specific and substantially larger than cached
            // type icons. Release them when virtualization recycles the row.
            if (item.IsImageFile)
            {
                // Do not raise binding notifications inside WinUI's recycling
                // callback. Sorting may realize this same item again immediately.
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (sender.ContainerFromItem(item) is not null) return;
                    item.ShellIcon = null;
                    item.ShellIconLoadRequested = false;
                });
            }
            return;
        }
        if (item.ShellIcon is not null || item.ShellIconLoadRequested)
        {
            return;
        }

        item.ShellIconLoadRequested = true;
        var container = args.ItemContainer;
        var icon = await _shellIcons.GetIconAsync(item);
        if (!ReferenceEquals(sender.ContainerFromItem(item), container)
            || !ReferenceEquals(container.Content, item))
        {
            item.ShellIconLoadRequested = false;
            return;
        }
        item.ShellIcon = icon;
    }

    private void DetachFileListHandlers(ListView list)
    {
        list.DragOver -= FileList_DragOver;
        list.DragLeave -= FileList_DragLeave;
        list.Drop -= FileList_Drop;
        list.DragItemsStarting -= FileList_DragItemsStarting;
        list.DragItemsCompleted -= FileList_DragItemsCompleted;
        list.DoubleTapped -= FileList_DoubleTapped;
        list.RightTapped -= FileList_RightTapped;
        list.SelectionChanged -= FileList_SelectionChanged;
        list.KeyDown -= FileList_KeyDown;
        list.ContainerContentChanging -= FileList_ContainerContentChanging;
    }

    private void ShowTabView(TabRuntime runtime)
    {
        foreach (var tabRuntime in Tabs.TabItems.OfType<TabViewItem>()
                     .Select(item => item.Tag).OfType<TabRuntime>())
        {
            if (tabRuntime.FileList is not { } list) continue;
            list.ContextFlyout = null;
            var active = ReferenceEquals(tabRuntime, runtime);
            // Keep the native drag source mounted until DragItemsCompleted.
            // Collapsing it during hover navigation can interrupt WinUI's drag.
            list.Visibility = active || ReferenceEquals(list, _fileDragSourceList)
                ? Visibility.Visible
                : Visibility.Collapsed;
            list.Opacity = active ? 1 : 0;
            list.IsHitTestVisible = active;
        }
        if (runtime.FileList is { } activeList)
        {
            // Right-click routing is handled explicitly so blank space cannot
            // inherit the file-selection context menu from the ListView.
            activeList.ContextFlyout = null;
            runtime.ScrollViewer ??= FindDescendant<ScrollViewer>(activeList);
            _fileScrollViewer = runtime.ScrollViewer;
        }
        ApplyViewMode(runtime);
        UpdateEmptyFolderState();
    }

    private void ApplyViewMode(TabRuntime runtime)
    {
        if (runtime.FileList is not { } list) return;
        var mode = runtime.Session.ViewMode == FileViewMode.Icons
            ? FileViewMode.MediumIcons
            : runtime.Session.ViewMode;
        var isDetails = mode == FileViewMode.Details;

        if (runtime.AppliedViewMode != mode)
        {
            var templateKey = mode switch
            {
                FileViewMode.ExtraLargeIcons => "ExtraLargeIconFileItemTemplate",
                FileViewMode.LargeIcons => "LargeIconFileItemTemplate",
                FileViewMode.MediumIcons => "MediumIconFileItemTemplate",
                FileViewMode.SmallIcons => "SmallIconFileItemTemplate",
                FileViewMode.List => "ListFileItemTemplate",
                FileViewMode.Tiles => "TileFileItemTemplate",
                FileViewMode.Content => "ContentFileItemTemplate",
                _ => "DetailsFileItemTemplate"
            };
            var panelKey = mode switch
            {
                FileViewMode.Details or FileViewMode.Content => "VerticalItemsPanel",
                FileViewMode.List or FileViewMode.SmallIcons => "ListItemsPanel",
                _ => "WrappingItemsPanel"
            };

            list.ItemTemplate = (DataTemplate)RootLayout.Resources[templateKey];
            list.ItemsPanel = (ItemsPanelTemplate)RootLayout.Resources[panelKey];
            if (isDetails)
            {
                list.ItemContainerStyle = null;
                list.ItemContainerStyleSelector =
                    (StyleSelector)RootLayout.Resources["FileRowStyleSelector"];
            }
            else
            {
                list.ItemContainerStyleSelector = null;
                list.ItemContainerStyle = (Style)RootLayout.Resources["FileTileItemStyle"];
            }
            Grid.SetRow(list, isDetails ? 1 : 0);
            Grid.SetRowSpan(list, isDetails ? 1 : 2);
            runtime.AppliedViewMode = mode;
            runtime.ScrollViewer = null;
        }

        if (ReferenceEquals(runtime, ActiveTab))
        {
            DetailsHeader.Visibility = isDetails ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetRow(EmptyFolderPanel, isDetails ? 1 : 0);
            Grid.SetRowSpan(EmptyFolderPanel, isDetails ? 1 : 2);
        }
    }

    private void UpdateEmptyFolderState()
    {
        if (ActiveTab is not { } tab
            || !string.Equals(tab.LoadedPath, tab.Session.CurrentPath, StringComparison.OrdinalIgnoreCase)
            || tab.Items.Count != 0)
        {
            EmptyFolderPanel.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyFolderMessage.Text = tab.Session.CurrentPath == SpecialLocations.ThisPc
            ? "No drives are available."
            : string.IsNullOrWhiteSpace(tab.LoadedSearch)
                ? "This folder is empty."
                : "No items match your search.";
        EmptyFolderPanel.Visibility = Visibility.Visible;
    }

    private void DestroyFileList(TabRuntime runtime)
    {
        if (runtime.FileList is not { } list) return;
        list.ContextFlyout = null;
        if (!ReferenceEquals(list, PrimaryFileList))
        {
            DetachFileListHandlers(list);
            FileViewHost.Children.Remove(list);
        }
        else
        {
            list.ItemsSource = null;
            list.Visibility = Visibility.Collapsed;
            _primaryFileListAssigned = false;
        }
        runtime.FileList = null;
    }

    private void AttachTabDropHandlers(TabViewItem item)
    {
        DetachTabDropHandlers(item);
        item.AllowDrop = true;
        item.DragLeave += ExplorerTab_DragLeave;
        item.PointerEntered += Tab_PointerEntered;
        item.PointerExited += Tab_PointerExited;
    }

    private void DetachTabDropHandlers(TabViewItem item)
    {
        item.DragLeave -= ExplorerTab_DragLeave;
        item.PointerEntered -= Tab_PointerEntered;
        item.PointerExited -= Tab_PointerExited;
    }

    internal bool ContainsTab(TabViewItem tab) => Tabs.TabItems.Contains(tab);
    internal bool OwnsTabView(TabView tabView) => ReferenceEquals(Tabs, tabView);
    internal IReadOnlyList<TabSession> GetTabSessions() => Tabs.TabItems
        .OfType<TabViewItem>()
        .Select(item => (item.Tag as TabRuntime)?.Session)
        .Where(session => session is not null)
        .Cast<TabSession>()
        .ToArray();

    private async Task NavigateAsync(string path, bool addHistory = true)
    {
        TabRuntime? tab = ActiveTab;
        if (tab is null)
        {
            return;
        }

        if (string.Equals(path, "This PC", StringComparison.OrdinalIgnoreCase))
        {
            path = SpecialLocations.ThisPc;
        }

        var navigationTimer = Stopwatch.StartNew();
        Breadcrumb("Navigate", $"{Shape(path)} addHistory={addHistory} search={!string.IsNullOrEmpty(SearchBox.Text)}");

        string canonical = SpecialLocations.ThisPc;
        if (path != SpecialLocations.ThisPc)
        {
            try
            {
                canonical = PathCanonicalizer.Canonicalize(path);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                await ShowErrorAsync("That path is not valid.", exception);
                return;
            }

            if (!Directory.Exists(canonical))
            {
                await ShowErrorAsync("That folder is unavailable.", new DirectoryNotFoundException(canonical));
                return;
            }
        }

        var preserveView = !addHistory
            && string.Equals(tab.Session.CurrentPath, canonical, StringComparison.OrdinalIgnoreCase);
        var preservedSelection = preserveView
            ? FileList.SelectedItems.Cast<FileSystemItemViewModel>().Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _fileScrollViewer ??= FindDescendant<ScrollViewer>(FileList);
        var preservedVerticalOffset = preserveView ? _fileScrollViewer?.VerticalOffset : null;

        _navigationCancellation?.Cancel();
        _navigationCancellation?.Dispose();
        _navigationCancellation = new CancellationTokenSource();
        var cancellationToken = _navigationCancellation.Token;

        var previous = tab.Session;
        if (addHistory && !string.Equals(previous.CurrentPath, canonical, StringComparison.OrdinalIgnoreCase))
        {
            tab.Session = previous with
            {
                CurrentPath = canonical,
                BackHistory = previous.BackHistory.Append(previous.CurrentPath).TakeLast(100).ToArray(),
                ForwardHistory = []
            };
        }
        else
        {
            tab.Session = previous with { CurrentPath = canonical };
        }

        UpdateAddressBar(canonical);
        if (((App)Application.Current).FolderSortPreferences.TryGetValue(canonical, out var folderSort))
            tab.Session = tab.Session with { SortProperty = folderSort.Property, SortDirection = folderSort.Direction };
        SynchronizeSidebarSelection(canonical);
        UpdateSortIndicators();
        UpdateCommandState();
        UpdateEmptyFolderState();
        if (Tabs.SelectedItem is TabViewItem tabItem)
        {
            tabItem.Header = GetTabTitle(canonical);
            DispatcherQueue.TryEnqueue(UpdateTabSelectionPresentation);
        }

        try
        {
            IReadOnlyList<FileSystemItemViewModel> entries;
            if (canonical == SpecialLocations.ThisPc)
            {
                var search = SearchBox.Text;
                entries = DriveService.ToViewModels(_drives)
                    .Where(item => string.IsNullOrWhiteSpace(search)
                        || item.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }
            else
            {
                entries = await _directoryService.EnumerateAsync(canonical, SearchBox.Text, cancellationToken);
            }

            entries = SortEntries(entries, tab.Session.SortProperty, tab.Session.SortDirection);

            tab.Items.Clear();
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                entry.RowIndex = index;
                tab.Items.Add(entry);
            }

            tab.LoadedPath = canonical;
            tab.LoadedSearch = SearchBox.Text;
            Breadcrumb("Navigate done", $"items={entries.Count} in {navigationTimer.ElapsedMilliseconds} ms");

            UpdateStatusText();
            UpdateEmptyFolderState();
            _ = PopulateFolderSizesAsync(
                tab,
                entries.Where(entry => entry.IsFolder && !entry.IsDrive).ToArray(),
                cancellationToken);
            if (preserveView)
            {
                RestoreFileViewState(preservedSelection, preservedVerticalOffset);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer navigation superseded this request.
            Breadcrumb("Navigate superseded", $"after {navigationTimer.ElapsedMilliseconds} ms");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await ShowErrorAsync("Anz Explorer could not open that folder.", exception);
        }
    }

    private static IReadOnlyList<FileSystemItemViewModel> SortEntries(
        IEnumerable<FileSystemItemViewModel> entries,
        string property,
        SortDirection direction)
    {
        var foldersFirst = entries.OrderByDescending(item => item.IsFolder);
        IOrderedEnumerable<FileSystemItemViewModel> sorted = (property, direction) switch
        {
            (nameof(FileSystemItemViewModel.Modified), SortDirection.Ascending) => foldersFirst.ThenBy(item => item.Modified),
            (nameof(FileSystemItemViewModel.Modified), SortDirection.Descending) => foldersFirst.ThenByDescending(item => item.Modified),
            (nameof(FileSystemItemViewModel.Type), SortDirection.Ascending) => foldersFirst.ThenBy(item => item.Type, StringComparer.CurrentCultureIgnoreCase),
            (nameof(FileSystemItemViewModel.Type), SortDirection.Descending) => foldersFirst.ThenByDescending(item => item.Type, StringComparer.CurrentCultureIgnoreCase),
            (nameof(FileSystemItemViewModel.LogicalBytes), SortDirection.Ascending) => foldersFirst.ThenBy(item => item.LogicalBytes ?? -1),
            (nameof(FileSystemItemViewModel.LogicalBytes), SortDirection.Descending) => foldersFirst.ThenByDescending(item => item.LogicalBytes ?? -1),
            (_, SortDirection.Descending) => foldersFirst.ThenByDescending(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => foldersFirst.ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
        };
        return sorted.ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private async void ColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string property } || ActiveTab is not { } tab) return;
        var direction = string.Equals(tab.Session.SortProperty, property, StringComparison.Ordinal)
            && tab.Session.SortDirection == SortDirection.Ascending
                ? SortDirection.Descending
                : SortDirection.Ascending;
        tab.Session = tab.Session with { SortProperty = property, SortDirection = direction };
        Breadcrumb("Sort (header)", $"{property} {direction} items={tab.Items.Count}");
        UpdateSortIndicators();
        ApplyCurrentSort();
        await SaveStateAsync();
    }

    private void ColumnSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string column })
        {
            ColumnLayout.Resize(column, e.HorizontalChange);
        }
    }

    private async void ColumnSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        ((App)Application.Current).ApplyColumnWidths(ColumnLayout.ToContract());
        await SaveStateAsync();
    }

    internal void ApplyColumnWidths(DetailsColumnWidths widths)
    {
        ColumnLayout.Apply(widths);
        _settings = _settings with { DetailsColumnWidths = widths };
    }

    private void ApplyCurrentSort()
    {
        if (ActiveTab is not { } tab) return;
        ((App)Application.Current).FolderSortPreferences[tab.Session.CurrentPath] = new(
            tab.Session.CurrentPath, tab.Session.SortProperty, tab.Session.SortDirection);
        var selectedPaths = FileList.SelectedItems
            .Cast<FileSystemItemViewModel>()
            .Select(item => item.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _fileScrollViewer ??= FindDescendant<ScrollViewer>(FileList);
        var offset = _fileScrollViewer?.VerticalOffset;
        var sorted = SortEntries(Items, tab.Session.SortProperty, tab.Session.SortDirection);
        var list = FileList;
        var items = tab.Items;
        // Detach once so WinUI does not process N intermediate collection states
        // and selection/recycling callbacks while the order is being rebuilt.
        list.ItemsSource = null;
        try
        {
            items.Clear();
            for (var index = 0; index < sorted.Count; index++)
            {
                sorted[index].RowIndex = index;
                items.Add(sorted[index]);
            }
        }
        finally { list.ItemsSource = items; }

        RestoreFileViewState(selectedPaths, offset);
    }

    private void UpdateSortIndicators()
    {
        NameSortIndicator.Text = string.Empty;
        ModifiedSortIndicator.Text = string.Empty;
        TypeSortIndicator.Text = string.Empty;
        SizeSortIndicator.Text = string.Empty;
        if (ActiveTab is not { } tab) return;

        var indicator = tab.Session.SortDirection == SortDirection.Ascending ? "↑" : "↓";
        switch (tab.Session.SortProperty)
        {
            case nameof(FileSystemItemViewModel.Modified): ModifiedSortIndicator.Text = indicator; break;
            case nameof(FileSystemItemViewModel.Type): TypeSortIndicator.Text = indicator; break;
            case nameof(FileSystemItemViewModel.LogicalBytes): SizeSortIndicator.Text = indicator; break;
            default: NameSortIndicator.Text = indicator; break;
        }
    }

    private async void RootLayout_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(RootLayout).Properties;
        if (_addressEditActive
            && (properties.IsLeftButtonPressed || properties.IsRightButtonPressed)
            && !IsWithinElement(e.OriginalSource as DependencyObject, AddressBarHost))
        {
            HideAddressEditor(restoreFileListFocus: false);
        }

        if (properties.IsXButton1Pressed)
        {
            e.Handled = true;
            await GoBackAsync();
        }
        else if (properties.IsXButton2Pressed)
        {
            e.Handled = true;
            await GoForwardAsync();
        }
        else if (properties.IsLeftButtonPressed
            && FindAncestor<TabViewItem>(e.OriginalSource as DependencyObject) is { } tab
            && Tabs.TabItems.Contains(tab)
            && !IsTabCloseButtonSource(e.OriginalSource))
        {
            BeginTabPointerTracking(tab, e);
        }
    }

    private async void RootLayout_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || !IsKeyDown(VirtualKey.Control)) return;
        if (e.Key == OemPlusKey || e.Key == VirtualKey.Add)
        {
            await ChangeDisplayScaleAsync(DisplayScaleStep);
            e.Handled = true;
        }
        else if (e.Key == OemMinusKey || e.Key == VirtualKey.Subtract)
        {
            await ChangeDisplayScaleAsync(-DisplayScaleStep);
            e.Handled = true;
        }
    }

    private void RestoreFileViewState(IReadOnlySet<string> selectedPaths, double? verticalOffset)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var item in Items.Where(item => selectedPaths.Contains(item.Path)))
            {
                FileList.SelectedItems.Add(item);
            }

            if (verticalOffset is { } offset)
            {
                _fileScrollViewer ??= FindDescendant<ScrollViewer>(FileList);
                _fileScrollViewer?.ChangeView(null, offset, null, disableAnimation: true);
            }
        });
    }

    private async Task PopulateFolderSizesAsync(
        TabRuntime tab,
        IReadOnlyList<FileSystemItemViewModel> folders,
        CancellationToken cancellationToken)
    {
        foreach (var folder in folders)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            _ = PopulateOneFolderSizeAsync(tab, folder, cancellationToken);
        }

        await Task.CompletedTask;
    }

    private async Task PopulateOneFolderSizeAsync(
        TabRuntime tab,
        FileSystemItemViewModel item,
        CancellationToken cancellationToken)
    {
        await _sizeConcurrency.WaitAsync(cancellationToken);
        try
        {
            item.SizeState = FolderSizeState.Scanning;
            FolderSizeResult result;
            try
            {
                result = await _indexer.GetFolderSizeAsync(item.Path, cancellationToken: cancellationToken);
            }
            catch (Exception exception) when (
                exception is IOException or TimeoutException or UnauthorizedAccessException
                    or InvalidDataException or System.Text.Json.JsonException)
            {
                // Worker missing, backing off, or returned a bad frame: scan in-process
                // rather than leaving the row stuck on "Calculating…".
                if (exception is not IOException || !_indexer.IsTemporarilyUnavailable)
                {
                    DiagnosticLog.Warn("FolderSize", "Indexer request failed; scanning in-process", exception);
                }
                result = await _fallbackScanner.ScanAsync(item.Path, cancellationToken: cancellationToken);
            }

            if (!cancellationToken.IsCancellationRequested && tab.Items.Contains(item))
            {
                item.LogicalBytes = result.LogicalBytes;
                item.SizeState = result.State;
                if (ReferenceEquals(ActiveTab, tab)
                    && tab.Session.SortProperty == nameof(FileSystemItemViewModel.LogicalBytes)
                    && tab.Items.Where(entry => entry.IsFolder && !entry.IsDrive).All(entry =>
                        entry.SizeState is not FolderSizeState.Queued and not FolderSizeState.Scanning))
                {
                    ApplyCurrentSort();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Navigation cancellation is expected.
        }
        finally
        {
            _sizeConcurrency.Release();
        }
    }

    private async void SidebarNavigation_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer?.Tag is SidebarItem item)
        {
            Breadcrumb("Sidebar click", item.TargetType.ToString());
            await NavigateAsync(item.Target);
        }
    }

    private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateTabSelectionPresentation();
        DispatcherQueue.TryEnqueue(UpdateTabSelectionPresentation);
        if (e.RemovedItems.OfType<TabViewItem>().FirstOrDefault()?.Tag is TabRuntime previous)
        {
            CaptureTabViewState(previous);
        }

        if (ActiveTab is not { } tab) return;
        Breadcrumb("Select tab", $"index={Tabs.SelectedIndex} tabs={Tabs.TabItems.Count} initializing={_initializing}");
        _searchCancellation?.Cancel();
        EndControlledDragScrolling();
        _pendingDropDestination = null;
        _restoringTabSearch = true;
        SearchBox.Text = tab.LoadedSearch ?? string.Empty;
        _restoringTabSearch = false;
        ShowTabView(tab);
        if (_initializing) return;
        UpdateAddressBar(tab.Session.CurrentPath);
        SynchronizeSidebarSelection(tab.Session.CurrentPath);
        UpdateSortIndicators();
        UpdateCommandState();

        if (string.Equals(tab.LoadedPath, tab.Session.CurrentPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(tab.LoadedSearch, SearchBox.Text, StringComparison.Ordinal))
        {
            UpdateStatusText();
            RestoreCachedTabViewState(tab);
            RestartIncompleteFolderSizes(tab);
            return;
        }

        await NavigateAsync(tab.Session.CurrentPath, false);
    }

    private void UpdateTabSelectionPresentation()
    {
        var selected = Tabs.SelectedItem;
        foreach (var item in Tabs.TabItems.OfType<TabViewItem>())
        {
            var isActive = ReferenceEquals(item, selected);
            item.Opacity = 1;
            item.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                isActive ? "AnzSurfaceRaisedBrush" : "AnzPaneBackgroundBrush"];
            item.FontWeight = isActive ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            Canvas.SetZIndex(item, isActive ? 1 : 0);
        }
        UpdateTabInputRegions();
    }

    private void UpdateTabInputRegions()
    {
        // Keep the current drop target alive through selection changes during a drag.
        if (FileTabDropSurface.Visibility == Visibility.Visible) return;
        if (RootLayout.XamlRoot is not { } root) return;
        var scale = root.RasterizationScale;
        var rectangles = new List<Windows.Graphics.RectInt32>();
        FileTabDropSurface.Children.Clear();
        foreach (var header in Tabs.TabItems.OfType<TabViewItem>())
        {
            if (header.ActualWidth <= 0) continue;
            var bounds = header.TransformToVisual(RootLayout).TransformBounds(
                new Windows.Foundation.Rect(0, 0, header.ActualWidth, header.ActualHeight));
            rectangles.Add(new((int)(bounds.X * scale), (int)(bounds.Y * scale),
                (int)Math.Ceiling(bounds.Width * scale), (int)Math.Ceiling(bounds.Height * scale)));
            var local = header.TransformToVisual(FileTabDropSurface).TransformBounds(
                new Windows.Foundation.Rect(0, 0, header.ActualWidth, header.ActualHeight));
            var target = new Border { Width = local.Width, Height = local.Height,
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), AllowDrop = true };
            Canvas.SetLeft(target, local.X);
            Canvas.SetTop(target, local.Y);
            FileTabDropSurface.Children.Add(target);
        }
        InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
            .SetRegionRects(NonClientRegionKind.Passthrough, rectangles.ToArray());
    }

    private void Tab_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is TabViewItem item && !ReferenceEquals(item, Tabs.SelectedItem))
            item.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AnzSurfaceBrush"];
    }

    private void Tab_PointerExited(object sender, PointerRoutedEventArgs e) => UpdateTabSelectionPresentation();

    private void Tabs_SizeChanged(object sender, SizeChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(UpdateTabSelectionPresentation);

    private void CaptureTabViewState(TabRuntime tab)
    {
        if (tab.FileList is not { } list) return;
        tab.ScrollViewer ??= FindDescendant<ScrollViewer>(list);
        tab.Session = tab.Session with
        {
            SelectedPath = list.SelectedItem is FileSystemItemViewModel selected ? selected.Path : null,
            ScrollOffset = tab.ScrollViewer?.VerticalOffset ?? 0
        };
    }

    private void RestoreCachedTabViewState(TabRuntime tab)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(ActiveTab, tab)) return;
            if (tab.FileList is not { } list) return;
            if (tab.Session.SelectedPath is { } selectedPath
                && tab.Items.FirstOrDefault(item => string.Equals(
                    item.Path, selectedPath, StringComparison.OrdinalIgnoreCase)) is { } selected)
            {
                list.SelectedItem = selected;
            }

            tab.ScrollViewer ??= FindDescendant<ScrollViewer>(list);
            _fileScrollViewer = tab.ScrollViewer;
            tab.ScrollViewer?.ChangeView(null, tab.Session.ScrollOffset, null, disableAnimation: true);
        });
    }

    private void RestartIncompleteFolderSizes(TabRuntime tab)
    {
        var incomplete = tab.Items.Where(item => item.IsFolder
            && !item.IsDrive
            && item.SizeState is FolderSizeState.Unknown or FolderSizeState.Queued or FolderSizeState.Scanning).ToArray();
        if (incomplete.Length == 0) return;

        _navigationCancellation?.Cancel();
        _navigationCancellation?.Dispose();
        _navigationCancellation = new CancellationTokenSource();
        _ = PopulateFolderSizesAsync(tab, incomplete, _navigationCancellation.Token);
    }

    private async void Tabs_AddTabButtonClick(TabView sender, object args)
    {
        AddTab(ResolveHomePath(), true);
        await SaveStateAsync();
    }

    private async void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        Breadcrumb("Close tab", $"index={sender.TabItems.IndexOf(args.Tab)} tabs={sender.TabItems.Count}");
        if (sender.TabItems.Count == 1)
        {
            Close();
            return;
        }

        sender.TabItems.Remove(args.Tab);
        if ((args.Tab as TabViewItem)?.Tag is TabRuntime runtime) DestroyFileList(runtime);
        await SaveStateAsync();
    }

    private void BeginTabPointerTracking(TabViewItem tab, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(RootLayout);

        _pointerDragTab = tab;
        _tabPointerId = args.Pointer.PointerId;
        _tabPointerStart = point.Position;
        _tabPointerTracking = RootLayout.CapturePointer(args.Pointer);
        _tabPointerDragging = false;
        Tabs.SelectedItem = tab;
        // Tabs live inside the custom title bar. Consume their pointer sequence so a
        // small click movement cannot leak into the non-client window-drag behavior.
        args.Handled = true;
    }

    private void ExplorerTab_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!_tabPointerTracking
            || args.Pointer.PointerId != _tabPointerId)
        {
            return;
        }

        var point = args.GetCurrentPoint(RootLayout);
        var deltaX = point.Position.X - _tabPointerStart.X;
        var deltaY = point.Position.Y - _tabPointerStart.Y;
        if (!_tabPointerDragging
            && Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY)) >= TabDragThreshold)
        {
            _tabPointerDragging = true;
            StatusText.Text = $"Moving {_pointerDragTab?.Header}…";
        }

        if (_tabPointerDragging && _pointerDragTab is { } tab)
        {
            UpdateTabDragPreview(tab, ToScreenPoint(point.Position));
        }

        args.Handled = true;
    }

    private async void ExplorerTab_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (!_tabPointerTracking
            || args.Pointer.PointerId != _tabPointerId
            || _pointerDragTab is not { } tab)
        {
            return;
        }

        var position = args.GetCurrentPoint(RootLayout).Position;
        var wasDragging = _tabPointerDragging;
        EndTabPointerTracking(args.Pointer);
        args.Handled = true;
        if (!wasDragging) return;

        await CompleteManualTabDropAsync(tab, ToScreenPoint(position));
    }

    private void ExplorerTab_PointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        // TabView can cancel its own internal gesture immediately after RootLayout
        // takes capture. The physical drag is still active in that case.
        if (args.GetCurrentPoint(RootLayout).Properties.IsLeftButtonPressed) return;

        if (_tabPointerTracking
            && args.Pointer.PointerId == _tabPointerId
            && _pointerDragTab is not null)
        {
            EndTabPointerTracking(args.Pointer);
            StatusText.Text = "Tab move cancelled.";
        }
    }

    private void EndTabPointerTracking(Microsoft.UI.Xaml.Input.Pointer pointer)
    {
        RootLayout.ReleasePointerCapture(pointer);
        CloseTabDragPreview();
        _pointerDragTab = null;
        _tabPointerTracking = false;
        _tabPointerDragging = false;
    }

    private void UpdateTabDragPreview(TabViewItem tab, Windows.Graphics.PointInt32 screenPoint)
    {
        _tabDragPreview ??= new TabDragPreviewWindow(tab.Header?.ToString() ?? "Tab");
        var targetWindow = ((App)Application.Current).FindWindowAtTabStrip(screenPoint, this);
        if (!ReferenceEquals(_tabDropHighlightWindow, targetWindow))
        {
            _tabDropHighlightWindow?.SetTabDropTargetHighlight(false);
            _tabDropHighlightWindow = targetWindow;
            _tabDropHighlightWindow?.SetTabDropTargetHighlight(true);
        }

        if (ReferenceEquals(targetWindow, this))
        {
            _tabDragPreview.ShowAt(screenPoint, "Reorder in this window", TabDragPreviewState.Reorder);
        }
        else if (targetWindow is not null)
        {
            var destination = targetWindow.ActiveTab?.Session.CurrentPath is { } path
                ? GetTabTitle(path)
                : "another Anz Explorer window";
            _tabDragPreview.ShowAt(screenPoint, $"Move to {destination}", TabDragPreviewState.Merge);
        }
        else
        {
            _tabDragPreview.ShowAt(screenPoint, "Open in a new window", TabDragPreviewState.TearOut);
        }
    }

    private void CloseTabDragPreview()
    {
        _tabDropHighlightWindow?.SetTabDropTargetHighlight(false);
        _tabDropHighlightWindow = null;
        if (_tabDragPreview is null) return;

        _tabDragPreview.Close();
        _tabDragPreview = null;
    }

    private void SetTabDropTargetHighlight(bool visible) =>
        TabDropTargetIndicator.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private async Task CompleteManualTabDropAsync(
        TabViewItem tab,
        Windows.Graphics.PointInt32 screenPoint)
    {
        var app = (App)Application.Current;
        var targetWindow = app.FindWindowAtTabStrip(screenPoint, this);
        Breadcrumb("Tab drop", ReferenceEquals(targetWindow, this) ? "reorder"
            : targetWindow is not null ? $"merge into W{targetWindow.DiagnosticId}" : "tear out to new window");
        if (ReferenceEquals(targetWindow, this))
        {
            ReorderTab(tab, GetTabDropIndex(screenPoint));
            StatusText.Text = "Tab reordered.";
            await SaveStateAsync();
            return;
        }

        if (targetWindow is not null)
        {
            var insertIndex = targetWindow.GetTabDropIndex(screenPoint);
            if (TransferTabTo(this, targetWindow, ref tab, insertIndex))
            {
                targetWindow.Tabs.SelectedItem = tab;
                targetWindow.Activate();
                QueueCloseIfNoTabsAfterTransfer();
                await targetWindow.SaveStateAsync();
            }
            else
            {
                DiagnosticLog.Warn("UI", $"W{DiagnosticId} Tab merge failed");
                StatusText.Text = "The tab could not be combined with that window.";
            }
            return;
        }

        var newWindow = app.CreateWindow(startEmpty: true, activate: false, tearOutSource: this);
        await newWindow._initializationTask;
        newWindow.PrepareForCompletedTearOut(AppWindow.Position, AppWindow.Size);
        if (!TransferTabTo(this, newWindow, ref tab, 0))
        {
            DiagnosticLog.Warn("UI", $"W{DiagnosticId} Tab tear-out failed");
            StatusText.Text = "The tab could not be moved to a new window.";
            newWindow._suppressStateSaveOnClose = true;
            newWindow.Close();
            return;
        }

        newWindow.Tabs.SelectedItem = tab;
        newWindow.Activate();
        QueueCloseIfNoTabsAfterTransfer();
        await newWindow.SaveStateAsync();
    }

    private void ReorderTab(TabViewItem tab, int requestedIndex)
    {
        var currentIndex = Tabs.TabItems.IndexOf(tab);
        if (currentIndex < 0) return;
        Tabs.TabItems.RemoveAt(currentIndex);
        var insertIndex = Math.Clamp(
            requestedIndex > currentIndex ? requestedIndex - 1 : requestedIndex,
            0,
            Tabs.TabItems.Count);
        Tabs.TabItems.Insert(insertIndex, tab);
        Tabs.SelectedItem = tab;
    }

    private int GetTabDropIndex(Windows.Graphics.PointInt32 screenPoint)
    {
        var scale = RootLayout.XamlRoot?.RasterizationScale ?? 1;
        var tabs = Tabs.TabItems.OfType<TabViewItem>().ToArray();
        for (var index = 0; index < tabs.Length; index++)
        {
            try
            {
                var bounds = tabs[index].TransformToVisual(RootLayout).TransformBounds(
                    new Windows.Foundation.Rect(0, 0, tabs[index].ActualWidth, tabs[index].ActualHeight));
                var midpoint = AppWindow.Position.X + ((bounds.X + (bounds.Width / 2)) * scale);
                if (screenPoint.X < midpoint) return index;
            }
            catch (InvalidOperationException)
            {
                // A tab can briefly be between visual parents during a transfer.
            }
        }
        return tabs.Length;
    }

    internal bool ContainsScreenPointInTabStrip(Windows.Graphics.PointInt32 screenPoint)
    {
        var scale = RootLayout.XamlRoot?.RasterizationScale ?? 1;
        var left = AppWindow.Position.X + (210 * scale);
        var right = AppWindow.Position.X + AppWindow.Size.Width - (140 * scale);
        var top = AppWindow.Position.Y;
        var bottom = top + (44 * scale);
        return screenPoint.X >= left && screenPoint.X <= right
            && screenPoint.Y >= top && screenPoint.Y <= bottom;
    }

    private Windows.Graphics.PointInt32 ToScreenPoint(Windows.Foundation.Point position)
    {
        var scale = RootLayout.XamlRoot?.RasterizationScale ?? 1;
        return new Windows.Graphics.PointInt32(
            AppWindow.Position.X + (int)Math.Round(position.X * scale),
            AppWindow.Position.Y + (int)Math.Round(position.Y * scale));
    }

    private static bool IsTabCloseButtonSource(object source)
    {
        for (var current = source as DependencyObject; current is not null;
             current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current))
        {
            if (current is Button button
                && string.Equals(
                    AutomationProperties.GetName(button),
                    "Close Tab",
                    StringComparison.Ordinal)) return true;
            if (current is TabViewItem) break;
        }
        return false;
    }

    private static bool TransferTabTo(
        MainWindow source,
        MainWindow target,
        ref TabViewItem tab,
        int insertIndex)
    {
        if (ReferenceEquals(source, target) || target.ContainsTab(tab)) return false;
        if (!source.ContainsTab(tab) || tab.Tag is not TabRuntime runtime) return false;

        var originalTab = tab;
        var originalIndex = source.Tabs.TabItems.IndexOf(tab);
        source.CaptureTabViewState(runtime);
        var replacement = new TabViewItem
        {
            Header = tab.Header,
            IconSource = new FontIconSource { Glyph = "\uE8B7" },
            Tag = runtime,
            IsClosable = true,
            MinWidth = MinimumTabWidth,
            MaxWidth = MaximumTabWidth,
            Style = (Style)target.RootLayout.Resources["ExplorerTabStyle"]
        };
        source.DetachTabDropHandlers(tab);
        try
        {
            source.Tabs.TabItems.Remove(tab);
            source.DestroyFileList(runtime);
            runtime.Owner = target;
            runtime.FileList = target.CreateFileList(runtime);
            runtime.ScrollViewer = null;
            target.AttachTabDropHandlers(replacement);
            target.Tabs.TabItems.Insert(
                TabTransferTransaction.ClampDropIndex(insertIndex, target.Tabs.TabItems.Count), replacement);
            tab = replacement;
            source.UpdateTabSelectionPresentation();
            target.UpdateTabSelectionPresentation();
            return true;
        }
        catch (Exception transferError)
        {
            DiagnosticLog.Error("UI", $"Tab transfer W{source.DiagnosticId} -> W{target.DiagnosticId} failed; rolling back", transferError);
            try
            {
                if (target.ContainsTab(replacement)) target.Tabs.TabItems.Remove(replacement);
                target.DestroyFileList(runtime);
                target.DetachTabDropHandlers(replacement);
                runtime.Owner = source;
                runtime.FileList = source.CreateFileList(runtime);
                runtime.ScrollViewer = null;
                source.AttachTabDropHandlers(originalTab);
                if (!source.ContainsTab(originalTab)) source.Tabs.TabItems.Insert(Math.Min(originalIndex, source.Tabs.TabItems.Count), originalTab);
                source.Tabs.SelectedItem = originalTab;
                tab = originalTab;
            }
            catch (Exception rollbackError)
            {
                // Keep this routed event from taking down the app if WinUI rejects reparenting.
                DiagnosticLog.Error("UI", "Tab transfer rollback failed", rollbackError);
            }
            return false;
        }
    }

    private void QueueCloseIfNoTabsAfterTransfer()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!TabTransferTransaction.ShouldCloseSource(Tabs.TabItems.Count)) return;
            _suppressStateSaveOnClose = true;
            Close();
        });
    }

    private void PrepareForCompletedTearOut(
        Windows.Graphics.PointInt32 sourcePosition,
        Windows.Graphics.SizeInt32 sourceSize)
    {
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        AppWindow.IsShownInSwitchers = true;
        AppWindow.Resize(sourceSize);
        AppWindow.Move(new Windows.Graphics.PointInt32(
            sourcePosition.X + 48,
            sourcePosition.Y + 48));
    }

    private void UpdateAddressBar(string path)
    {
        var displayPath = path == SpecialLocations.ThisPc ? "This PC" : path;
        AddressBox.Text = displayPath;
        _addressEditActive = false;
        AddressBox.Visibility = Visibility.Collapsed;
        BreadcrumbScroller.Visibility = Visibility.Visible;
        RebuildBreadcrumbs(path);
    }

    private void RebuildBreadcrumbs(string path)
    {
        BreadcrumbPanel.Children.Clear();
        var segments = CreateBreadcrumbSegments(path);
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            if (index > 0)
            {
                BreadcrumbPanel.Children.Add(new TextBlock
                {
                    Text = "\uE76C",
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                    FontSize = 9,
                    Width = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalTextAlignment = TextAlignment.Center,
                    Opacity = 0.72,
                    IsHitTestVisible = false
                });
            }

            var button = new Button
            {
                Content = segment.Label,
                Tag = segment.Target,
                Style = (Style)RootLayout.Resources["BreadcrumbButtonStyle"],
                MaxWidth = 220
            };
            AutomationProperties.SetName(button, $"Navigate to {segment.Label}");
            ToolTipService.SetToolTip(button, segment.Target == SpecialLocations.ThisPc ? "This PC" : segment.Target);
            button.Click += Breadcrumb_Click;
            BreadcrumbPanel.Children.Add(button);
        }

        DispatcherQueue.TryEnqueue(() =>
            BreadcrumbScroller.ChangeView(BreadcrumbScroller.ScrollableWidth, null, null, disableAnimation: true));
    }

    private IReadOnlyList<(string Label, string Target)> CreateBreadcrumbSegments(string path)
    {
        var segments = new List<(string Label, string Target)> { ("This PC", SpecialLocations.ThisPc) };
        if (path == SpecialLocations.ThisPc) return segments;

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root))
        {
            segments.Add((path, path));
            return segments;
        }

        var drive = _drives.FirstOrDefault(candidate =>
            string.Equals(candidate.RootPath, root, StringComparison.OrdinalIgnoreCase));
        var rootLabel = drive?.DisplayName;
        if (string.IsNullOrWhiteSpace(rootLabel))
        {
            rootLabel = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(rootLabel)) rootLabel = root;
        }

        segments.Add((rootLabel, root));
        var current = root;
        var remainder = path[root.Length..];
        foreach (var part in remainder.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            segments.Add((part, current));
        }

        return segments;
    }

    private async void Breadcrumb_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string target })
        {
            await NavigateAsync(target);
        }
    }

    private void AddressBarHost_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_addressEditActive
            || FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        ShowAddressEditor();
        e.Handled = true;
    }

    private void ShowAddressEditor()
    {
        if (ActiveTab is not { } tab) return;
        _addressEditActive = true;
        AddressBox.Text = tab.Session.CurrentPath == SpecialLocations.ThisPc
            ? "This PC"
            : tab.Session.CurrentPath;
        BreadcrumbScroller.Visibility = Visibility.Collapsed;
        AddressBox.Visibility = Visibility.Visible;
        AddressBox.Focus(FocusState.Keyboard);
        AddressBox.SelectAll();
    }

    private void HideAddressEditor(bool restoreFileListFocus = true)
    {
        if (!_addressEditActive) return;
        var path = ActiveTab?.Session.CurrentPath ?? SpecialLocations.ThisPc;
        UpdateAddressBar(path);
        if (restoreFileListFocus)
        {
            FileList.Focus(FocusState.Programmatic);
        }
    }

    private async void AddressBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await NavigateAsync(AddressBox.Text);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            HideAddressEditor();
        }
    }

    private void AddressBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_addressEditActive) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_addressEditActive && AddressBox.FocusState == FocusState.Unfocused)
            {
                var path = ActiveTab?.Session.CurrentPath ?? SpecialLocations.ThisPc;
                UpdateAddressBar(path);
            }
        });
    }

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing || _restoringTabSearch || ActiveTab is null)
        {
            return;
        }

        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        var token = _searchCancellation.Token;
        try
        {
            await Task.Delay(250, token);
            await NavigateAsync(ActiveTab.Session.CurrentPath, false);
        }
        catch (OperationCanceledException)
        {
            // A more recent search term superseded this one.
        }
    }

    private async void FileList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: FileSystemItemViewModel item })
        {
            FileList.SelectedItem = item;
            await OpenSelectedAsync();
        }
    }

    private void FileViewHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(FileViewHost);
        if (!point.Properties.IsLeftButtonPressed
            || point.Position.Y <= 30
            || FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject) is not null
            || FindAncestor<Microsoft.UI.Xaml.Controls.Primitives.ScrollBar>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        _marqueePointerId = e.Pointer.PointerId;
        _marqueeStart = point.Position;
        _marqueePending = true;
        _marqueeActive = false;
        _marqueeToggle = IsKeyDown(VirtualKey.Control);
        _marqueeExtend = IsKeyDown(VirtualKey.Shift);
        _marqueeBaseSelection.Clear();
        foreach (var item in FileList.SelectedItems.Cast<FileSystemItemViewModel>())
        {
            _marqueeBaseSelection.Add(item);
        }

        FileViewHost.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void FileViewHost_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_marqueePending || e.Pointer.PointerId != _marqueePointerId) return;
        var position = e.GetCurrentPoint(FileViewHost).Position;
        if (!_marqueeActive
            && Math.Abs(position.X - _marqueeStart.X) < 4
            && Math.Abs(position.Y - _marqueeStart.Y) < 4)
        {
            return;
        }

        _marqueeActive = true;
        SelectionMarquee.Visibility = Visibility.Visible;
        UpdateMarquee(position);
        e.Handled = true;
    }

    private void FileViewHost_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_marqueePending || e.Pointer.PointerId != _marqueePointerId) return;
        if (_marqueeActive)
        {
            UpdateMarquee(e.GetCurrentPoint(FileViewHost).Position);
        }
        else if (!_marqueeToggle && !_marqueeExtend)
        {
            FileList.SelectedItems.Clear();
        }

        EndMarquee(e.Pointer);
        e.Handled = true;
    }

    private void FileViewHost_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_marqueePending && e.Pointer.PointerId == _marqueePointerId)
        {
            EndMarquee(e.Pointer);
        }
    }

    private void UpdateMarquee(Windows.Foundation.Point current)
    {
        var left = Math.Max(0, Math.Min(_marqueeStart.X, current.X));
        var top = Math.Max(30, Math.Min(_marqueeStart.Y, current.Y));
        var right = Math.Min(FileViewHost.ActualWidth, Math.Max(_marqueeStart.X, current.X));
        var bottom = Math.Min(FileViewHost.ActualHeight, Math.Max(_marqueeStart.Y, current.Y));
        var selectionRect = new Windows.Foundation.Rect(
            left,
            top,
            Math.Max(0, right - left),
            Math.Max(0, bottom - top));

        Canvas.SetLeft(SelectionMarquee, selectionRect.X);
        Canvas.SetTop(SelectionMarquee, selectionRect.Y);
        SelectionMarquee.Width = selectionRect.Width;
        SelectionMarquee.Height = selectionRect.Height;

        foreach (var item in Items)
        {
            if (FileList.ContainerFromItem(item) is not ListViewItem container) continue;
            var origin = container.TransformToVisual(FileViewHost)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            var itemRect = new Windows.Foundation.Rect(
                origin.X,
                origin.Y,
                container.ActualWidth,
                container.ActualHeight);
            var intersects = RectanglesIntersect(selectionRect, itemRect);
            var shouldSelect = _marqueeToggle
                ? _marqueeBaseSelection.Contains(item) != intersects
                : _marqueeExtend
                    ? _marqueeBaseSelection.Contains(item) || intersects
                    : intersects;
            var isSelected = FileList.SelectedItems.Contains(item);
            if (shouldSelect && !isSelected) FileList.SelectedItems.Add(item);
            else if (!shouldSelect && isSelected) FileList.SelectedItems.Remove(item);
        }
    }

    private void EndMarquee(Microsoft.UI.Xaml.Input.Pointer pointer)
    {
        FileViewHost.ReleasePointerCapture(pointer);
        SelectionMarquee.Visibility = Visibility.Collapsed;
        _marqueePending = false;
        _marqueeActive = false;
        _marqueeBaseSelection.Clear();
    }

    private static bool RectanglesIntersect(Windows.Foundation.Rect first, Windows.Foundation.Rect second) =>
        first.Left <= second.Right
        && first.Right >= second.Left
        && first.Top <= second.Bottom
        && first.Bottom >= second.Top;

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static bool IsWithinElement(DependencyObject? current, DependencyObject ancestor)
    {
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor)) return true;
            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static T? FindDescendant<T>(DependencyObject current) where T : DependencyObject
    {
        var childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(current);
        for (var index = 0; index < childCount; index++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(current, index);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } descendant) return descendant;
        }

        return null;
    }

    private void FileList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var sources = e.Items.OfType<FileSystemItemViewModel>().ToArray();
        if (sources.Length == 0)
        {
            e.Cancel = true;
            return;
        }

        Breadcrumb("File drag start", $"items={sources.Length} folders={sources.Count(source => source.IsFolder)}");
        _draggedItems = sources;
        _fileDragSourceList = sender as ListView;
        _fileDragSourceTab = ActiveTab;
        UpdateTabInputRegions();
        FileTabDropSurface.Visibility = Visibility.Visible;
        // Fix header widths for the gesture, including while selection changes.
        foreach (var header in Tabs.TabItems.OfType<TabViewItem>())
            if (header.ActualWidth > 0) header.Width = header.ActualWidth;
        _dropHandledInApp = false;
        _pendingDropDestination = null;
        BeginControlledDragScrolling();
        StatusText.Text = sources.Length == 1
            ? $"Dragging {sources[0].Name}…"
            : $"Dragging {sources.Length} items…";
        e.Data.RequestedOperation = DataPackageOperation.Move;
        e.Data.Properties.Title = sources.Length == 1 ? sources[0].Name : $"{sources.Length} items";
        e.Data.SetText(string.Join(Environment.NewLine, sources.Select(source => source.Path)));
        e.Data.SetData(InternalFilePathsFormat, string.Join(Environment.NewLine, sources.Select(source => source.Path)));
        e.Data.SetDataProvider(
            StandardDataFormats.StorageItems,
            request => ProvideDragStorageItemsAsync(request, sources));
    }

    private static async void ProvideDragStorageItemsAsync(
        DataProviderRequest request,
        IReadOnlyList<FileSystemItemViewModel> sources)
    {
        var deferral = request.GetDeferral();
        try
        {
            request.SetData(await FileTransferService.GetStorageItemsAsync(sources));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            request.SetData(Array.Empty<Windows.Storage.IStorageItem>());
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void FileList_DragItemsCompleted(object sender, DragItemsCompletedEventArgs args)
    {
        Breadcrumb("File drag end", $"result={args.DropResult} handledInApp={_dropHandledInApp}");
        EndControlledDragScrolling();
        _tabHoverTimer.Stop();
        _hoveredDropTab = null;
        var sourceTab = _fileDragSourceTab;
        _fileDragSourceList = null;
        FileTabDropSurface.Visibility = Visibility.Collapsed;
        _fileDragSourceTab = null;
        foreach (var header in Tabs.TabItems.OfType<TabViewItem>())
            header.ClearValue(FrameworkElement.WidthProperty);
        if (ActiveTab is { } visibleTab) ShowTabView(visibleTab);
        var handledInApp = _dropHandledInApp;
        _dropHandledInApp = false;
        _pendingDropDestination = null;
        _draggedItems = [];
        if (!handledInApp && args.DropResult == DataPackageOperation.Move && sourceTab is { } tab)
        {
            tab.LoadedPath = null;
            if (ReferenceEquals(tab, ActiveTab)) await NavigateAsync(tab.Session.CurrentPath, false);
        }
        else
        {
            UpdateStatusText();
        }
    }

    private void FileList_DragOver(object sender, DragEventArgs e)
    {
        _tabHoverTimer.Stop();
        _hoveredDropTab = null;
        try
        {
            BeginControlledDragScrolling();
            _dragPosition = e.GetPosition(FileList);
            _pendingDropDestination = null;
            if (!CanAcceptStorageDrop(e, out var destination)) return;
            _pendingDropDestination = destination;
            var operation = ResolveDropOperation(e, destination);
            e.AcceptedOperation = operation;
            e.DragUIOverride.Caption = $"{(operation == DataPackageOperation.Move ? "Move" : "Copy")} to {GetTabTitle(destination)}";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
            e.Handled = true;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Warn("UI", $"W{DiagnosticId} File list drag-over rejected", exception);
            _pendingDropDestination = null;
            e.AcceptedOperation = DataPackageOperation.None;
            StatusText.Text = "This item cannot be dropped here.";
        }
    }

    private void FileList_DragLeave(object sender, DragEventArgs e)
    {
        if (!ReferenceEquals(sender, FileList)) return;
        var position = e.GetPosition(FileList);
        if (position.X < 0
            || position.Y < 0
            || position.X > FileList.ActualWidth
            || position.Y > FileList.ActualHeight)
        {
            _pendingDropDestination = null;
            EndControlledDragScrolling();
        }
    }

    private async void FileList_Drop(object sender, DragEventArgs e)
    {
        try
        {
            var destination = _pendingDropDestination;
            if (string.IsNullOrWhiteSpace(destination) || !Directory.Exists(destination))
            {
                if (!CanAcceptStorageDrop(e, out destination)) return;
            }

            _pendingDropDestination = null;
            var operation = ResolveDropOperation(e, destination);
            _dropHandledInApp = true;
            EndControlledDragScrolling();
            e.AcceptedOperation = operation;
            e.Handled = true;
            await CompleteDropAsync(e, destination, operation);
        }
        catch (Exception exception)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            await ShowErrorAsync("The dropped items could not be transferred.", exception);
        }
    }

    private void SidebarItem_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not NavigationViewItem { Tag: SidebarItem item }
            || item.Target == SpecialLocations.ThisPc
            || !Directory.Exists(item.Target)
            || !ContainsSupportedDropData(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        var operation = ResolveDropOperation(e, item.Target);
        e.AcceptedOperation = operation;
        e.DragUIOverride.Caption = $"{(operation == DataPackageOperation.Move ? "Move" : "Copy")} to {item.Label}";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
        ((NavigationViewItem)sender).Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
    }

    private static void SidebarItem_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is NavigationViewItem navigationItem)
        {
            navigationItem.ClearValue(Control.BackgroundProperty);
        }
    }

    private async void SidebarItem_Drop(object sender, DragEventArgs e)
    {
        if (sender is not NavigationViewItem { Tag: SidebarItem item }
            || item.Target == SpecialLocations.ThisPc
            || !Directory.Exists(item.Target))
        {
            return;
        }

        ((NavigationViewItem)sender).ClearValue(Control.BackgroundProperty);
        _dropHandledInApp = true;
        EndControlledDragScrolling();
        await CompleteDropAsync(e, item.Target, ResolveDropOperation(e, item.Target));
    }

    private string? GetUpDropDestination()
    {
        if (ActiveTab is not { } tab || tab.Session.CurrentPath == SpecialLocations.ThisPc) return null;
        return Directory.GetParent(tab.Session.CurrentPath)?.FullName;
    }

    private void UpButton_DragOver(object sender, DragEventArgs e)
    {
        var destination = GetUpDropDestination();
        if (destination is null || !CanAcceptDropAt(e, destination))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.Handled = true;
        var operation = ResolveDropOperation(e, destination);
        e.AcceptedOperation = operation;
        e.DragUIOverride.Caption = $"{(operation == DataPackageOperation.Move ? "Move" : "Copy")} to {GetTabTitle(destination)}";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
        UpButton.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
    }

    private void UpButton_DragLeave(object sender, DragEventArgs e) =>
        UpButton.ClearValue(Control.BackgroundProperty);

    private async void UpButton_Drop(object sender, DragEventArgs e)
    {
        UpButton.ClearValue(Control.BackgroundProperty);
        var destination = GetUpDropDestination();
        if (destination is null || !CanAcceptDropAt(e, destination)) return;

        e.Handled = true;
        _dropHandledInApp = true;
        EndControlledDragScrolling();
        var operation = ResolveDropOperation(e, destination);
        e.AcceptedOperation = operation;
        await CompleteDropAsync(e, destination, operation);
    }

    private TabViewItem? FindFileDropTab(DragEventArgs e)
    {
        var stripPosition = e.GetPosition(Tabs);
        if (stripPosition.X < 0 || stripPosition.Y < 0
            || stripPosition.X >= Tabs.ActualWidth || stripPosition.Y >= Tabs.ActualHeight)
            return null;
        foreach (var header in Tabs.TabItems.OfType<TabViewItem>())
        {
            var point = e.GetPosition(header);
            if (point.X >= 0 && point.Y >= 0
                && point.X < header.ActualWidth && point.Y < header.ActualHeight)
                return header;
        }
        return null;
    }

    private void TabStripFile_DragOver(object sender, DragEventArgs e)
    {
        if (FindFileDropTab(e) is { } tab) ExplorerTab_DragOver(tab, e);
    }

    private void TabStripFile_Drop(object sender, DragEventArgs e)
    {
        if (FindFileDropTab(e) is { } tab) ExplorerTab_Drop(tab, e);
    }

    private void ExplorerTab_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not TabViewItem { Tag: TabRuntime runtime } tab
            || !ContainsSupportedDropData(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.Handled = true;
        EndControlledDragScrolling();
        _pendingDropDestination = null;
        var canDrop = runtime.Session.CurrentPath != SpecialLocations.ThisPc
            && CanAcceptDropAt(e, runtime.Session.CurrentPath);
        var operation = canDrop ? ResolveDropOperation(e, runtime.Session.CurrentPath) : DataPackageOperation.None;
        e.AcceptedOperation = operation;
        e.DragUIOverride.Caption = canDrop
            ? $"{(operation == DataPackageOperation.Move ? "Move" : "Copy")} to {GetTabTitle(runtime.Session.CurrentPath)}"
            : $"Switch to {GetTabTitle(runtime.Session.CurrentPath)}";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
        if (!ReferenceEquals(Tabs.SelectedItem, tab))
        {
            _tabHoverTimer.Stop();
            _hoveredDropTab = null;
            Tabs.SelectedItem = tab;
        }
    }

    private void ExplorerTab_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is not TabViewItem tab || _hoveredDropTab != tab) return;
        var position = e.GetPosition(tab);
        if (position.X < 0 || position.Y < 0 || position.X > tab.ActualWidth || position.Y > tab.ActualHeight)
        {
            _tabHoverTimer.Stop();
            _hoveredDropTab = null;
        }
    }

    private async void ExplorerTab_Drop(object sender, DragEventArgs e)
    {
        _tabHoverTimer.Stop();
        _pendingDropDestination = null;
        _hoveredDropTab = null;
        if (sender is not TabViewItem { Tag: TabRuntime runtime } targetTab
            || runtime.Session.CurrentPath == SpecialLocations.ThisPc
            || !CanAcceptDropAt(e, runtime.Session.CurrentPath))
        {
            return;
        }

        e.Handled = true;
        _dropHandledInApp = true;
        Tabs.SelectedItem = targetTab;
        var operation = ResolveDropOperation(e, runtime.Session.CurrentPath);
        e.AcceptedOperation = operation;
        await CompleteDropAsync(e, runtime.Session.CurrentPath, operation);
    }

    private void TabHoverTimer_Tick(object? sender, object e)
    {
        _tabHoverTimer.Stop();
        if (_hoveredDropTab is { } tab && Tabs.TabItems.Contains(tab))
        {
            _hoveredDropTab = null;
            EndControlledDragScrolling();
            _pendingDropDestination = null;
            Tabs.SelectedItem = tab;
        }
    }

    private bool CanAcceptStorageDrop(DragEventArgs e, out string destination)
    {
        destination = string.Empty;
        if (!ContainsSupportedDropData(e.DataView) || ActiveTab is null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return false;
        }

        if (ActiveTab.Session.CurrentPath != SpecialLocations.ThisPc)
        {
            destination = ActiveTab.Session.CurrentPath;
        }

        return CanAcceptDropAt(e, destination);
    }

    private bool CanAcceptDropAt(DragEventArgs e, string destination)
    {
        if (!ContainsSupportedDropData(e.DataView)
            || string.IsNullOrWhiteSpace(destination)
            || !Directory.Exists(destination))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return false;
        }

        var resolvedDestination = destination;
        if (_draggedItems.Any(item =>
                item.IsFolder && PathCanonicalizer.IsSameOrDescendant(resolvedDestination, item.Path)))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return false;
        }

        if (_draggedItems.Count > 0
            && _draggedItems.All(item => PathsEqual(
                item.Path,
                Path.Combine(resolvedDestination, item.Name))))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return false;
        }

        return true;
    }

    private void FileRow_DragOver(object sender, DragEventArgs e)
    {
        _tabHoverTimer.Stop();
        _hoveredDropTab = null;
        e.Handled = true;
        try
        {
            if (sender is not FrameworkElement
                {
                    DataContext: FileSystemItemViewModel { IsFolder: true } targetFolder
                })
            {
                RejectDrop(e);
                return;
            }

            BeginControlledDragScrolling();
            _dragPosition = e.GetPosition(FileList);
            if (!CanAcceptDropAt(e, targetFolder.Path))
            {
                RejectDrop(e);
                return;
            }

            _pendingDropDestination = targetFolder.Path;
            var operation = ResolveDropOperation(e, targetFolder.Path);
            e.AcceptedOperation = operation;
            e.DragUIOverride.Caption = $"{(operation == DataPackageOperation.Move ? "Move" : "Copy")} to {targetFolder.Name}";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Warn("UI", $"W{DiagnosticId} Row drag-over rejected", exception);
            RejectDrop(e);
            StatusText.Text = "This item cannot be dropped here.";
        }
    }

    private async void FileRow_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        try
        {
            if (sender is not FrameworkElement
                {
                    DataContext: FileSystemItemViewModel { IsFolder: true } targetFolder
                }
                || !CanAcceptDropAt(e, targetFolder.Path))
            {
                RejectDrop(e);
                EndControlledDragScrolling();
                return;
            }

            var operation = ResolveDropOperation(e, targetFolder.Path);
            _pendingDropDestination = null;
            _dropHandledInApp = true;
            EndControlledDragScrolling();
            e.AcceptedOperation = operation;
            await CompleteDropAsync(e, targetFolder.Path, operation);
        }
        catch (Exception exception)
        {
            RejectDrop(e);
            EndControlledDragScrolling();
            await ShowErrorAsync("The dropped items could not be transferred.", exception);
        }
    }

    private void RejectDrop(DragEventArgs e)
    {
        _pendingDropDestination = null;
        e.AcceptedOperation = DataPackageOperation.None;
    }

    private static bool PathsEqual(string first, string second) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
        StringComparison.OrdinalIgnoreCase);

    private void BeginControlledDragScrolling()
    {
        if (_controlledDragScrolling) return;
        _fileScrollViewer ??= FindDescendant<ScrollViewer>(FileList);
        if (_fileScrollViewer is null) return;

        _previousVerticalScrollMode = _fileScrollViewer.VerticalScrollMode;
        _fileScrollViewer.VerticalScrollMode = ScrollMode.Disabled;
        _controlledDragScrolling = true;
        _dragScrollTimer.Start();
    }

    private void EndControlledDragScrolling()
    {
        _dragScrollTimer.Stop();
        _dragPosition = null;
        if (_controlledDragScrolling && _fileScrollViewer is not null)
        {
            _fileScrollViewer.VerticalScrollMode = _previousVerticalScrollMode;
        }

        _controlledDragScrolling = false;
    }

    private void DragScrollTimer_Tick(object? sender, object e)
    {
        if (_fileScrollViewer is null || _dragPosition is not { } position) return;

        const double activationBand = 40;
        const double minimumScrollStep = 6;
        const double maximumScrollStep = 52;
        const double contentTop = 30;
        double? targetOffset = null;
        if (position.Y <= contentTop + activationBand)
        {
            var proximity = Math.Clamp(
                (contentTop + activationBand - position.Y) / activationBand,
                0,
                1);
            var scrollStep = minimumScrollStep
                + ((maximumScrollStep - minimumScrollStep) * proximity * proximity);
            targetOffset = Math.Max(0, _fileScrollViewer.VerticalOffset - scrollStep);
        }
        else if (position.Y >= FileList.ActualHeight - activationBand)
        {
            var proximity = Math.Clamp(
                (position.Y - (FileList.ActualHeight - activationBand)) / activationBand,
                0,
                1);
            var scrollStep = minimumScrollStep
                + ((maximumScrollStep - minimumScrollStep) * proximity * proximity);
            targetOffset = Math.Min(
                _fileScrollViewer.ScrollableHeight,
                _fileScrollViewer.VerticalOffset + scrollStep);
        }

        if (targetOffset is { } offset && Math.Abs(offset - _fileScrollViewer.VerticalOffset) > 0.1)
        {
            _fileScrollViewer.ChangeView(null, offset, null, disableAnimation: true);
        }
    }

    private DataPackageOperation ResolveDropOperation(DragEventArgs e, string destination)
    {
        if ((e.Modifiers & DataTransferDragDropModifiers.Control) == DataTransferDragDropModifiers.Control)
            return DataPackageOperation.Copy;
        if ((e.Modifiers & DataTransferDragDropModifiers.Shift) == DataTransferDragDropModifiers.Shift)
            return DataPackageOperation.Move;

        if (_draggedItems.Count > 0)
        {
            var destinationRoot = Path.GetPathRoot(destination);
            return _draggedItems.All(item => string.Equals(
                    Path.GetPathRoot(item.Path), destinationRoot, StringComparison.OrdinalIgnoreCase))
                ? DataPackageOperation.Move
                : DataPackageOperation.Copy;
        }

        return e.DataView.RequestedOperation == DataPackageOperation.Move
            ? DataPackageOperation.Move
            : DataPackageOperation.Copy;
    }

    private static bool ContainsSupportedDropData(DataPackageView dataView) =>
        dataView.Contains(StandardDataFormats.StorageItems)
        || dataView.Contains(InternalFilePathsFormat);

    private async Task CompleteDropAsync(
        DragEventArgs e,
        string destination,
        DataPackageOperation operation)
    {
        var deferral = e.GetDeferral();
        _tabHoverTimer.Stop();
        _hoveredDropTab = null;
        var sourceTab = _fileDragSourceTab;
        var draggedItems = _draggedItems.ToArray();
        Breadcrumb("Drop", $"{operation} internalItems={draggedItems.Length} to {Shape(destination)}");
        StatusText.Text = $"{(operation == DataPackageOperation.Move ? "Moving" : "Copying")} items…";
        try
        {
            var storageItems = draggedItems.Length > 0
                ? await FileTransferService.GetStorageItemsAsync(draggedItems)
                : await e.DataView.GetStorageItemsAsync();
            var failures = await FileTransferService.TransferAsync(
                storageItems,
                destination,
                operation,
                CancellationToken.None);

            // A partial move also changes the source. Re-enumerate when revisited.
            if (sourceTab is not null) sourceTab.LoadedPath = null;
            foreach (var header in Tabs.TabItems.OfType<TabViewItem>())
                if (header.Tag is TabRuntime cached && !ReferenceEquals(cached, ActiveTab)
                    && cached.Session.CurrentPath != SpecialLocations.ThisPc
                    && (PathsEqual(cached.Session.CurrentPath, destination)
                        || draggedItems.Any(item => string.Equals(Path.GetDirectoryName(item.Path),
                            cached.Session.CurrentPath, StringComparison.OrdinalIgnoreCase))))
                    cached.LoadedPath = null;

            if (ActiveTab is { } tab)
            {
                await NavigateAsync(tab.Session.CurrentPath, false);
            }

            if (failures.Count > 0)
            {
                await ShowErrorAsync(
                    $"{failures.Count} item{(failures.Count == 1 ? string.Empty : "s")} could not be transferred.",
                    new IOException(failures[0]));
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await ShowErrorAsync("The dropped items could not be transferred.", exception);
        }
        finally
        {
            _draggedItems = [];
            deferral.Complete();
        }
    }

    private async void OpenSelected_Click(object sender, RoutedEventArgs e) => await OpenSelectedAsync();

    private void OpenSelectedNewTab_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is FileSystemItemViewModel { IsFolder: true } selected)
        {
            AddTab(selected.Path, true);
        }
    }

    private void PopulateOpenWithMenu(FileSystemItemViewModel? selected, bool exactlyOne)
    {
        OpenWithMenu.Items.Clear();
        var canOpenWith = exactlyOne && selected is { IsFolder: false, IsDrive: false };
        OpenWithMenu.Visibility = canOpenWith ? Visibility.Visible : Visibility.Collapsed;
        if (!canOpenWith || selected is null) return;

        try
        {
            foreach (var application in OpenWithService.GetRecommendedApplications(selected.Path))
            {
                var item = new MenuFlyoutItem
                {
                    Text = application.DisplayName,
                    Tag = new OpenWithRequest(application, selected.Path),
                    Icon = new FontIcon { Glyph = "\uE8E5" }
                };
                item.Click += OpenWithApplication_Click;
                OpenWithMenu.Items.Add(item);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // The Windows chooser remains available even if an association registry
            // provider is inaccessible or malformed.
        }

        if (OpenWithMenu.Items.Count > 0) OpenWithMenu.Items.Add(new MenuFlyoutSeparator());
        var choose = new MenuFlyoutItem
        {
            Text = "Choose another app",
            Tag = selected.Path,
            Icon = new FontIcon { Glyph = "\uE71D" }
        };
        choose.Click += ChooseAnotherApplication_Click;
        OpenWithMenu.Items.Add(choose);
    }

    private async void OpenWithApplication_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: OpenWithRequest request }) return;
        Breadcrumb("Open with", Path.GetFileName(request.Application.ExecutablePath));
        try
        {
            OpenWithService.Open(request.Application, request.Path);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await ShowErrorAsync($"{request.Application.DisplayName} could not open that file.", exception);
        }
    }

    private async void ChooseAnotherApplication_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string path }) return;
        try
        {
            OpenWithService.ShowChooser(WinRT.Interop.WindowNative.GetWindowHandle(this), path);
        }
        catch (Exception exception) when (
            exception is System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception)
        {
            await ShowErrorAsync("Windows could not show the app chooser.", exception);
        }
    }

    private async Task OpenSelectedAsync()
    {
        if (FileList.SelectedItem is not FileSystemItemViewModel selected)
        {
            return;
        }

        if (selected.IsFolder)
        {
            await NavigateAsync(selected.Path);
            return;
        }

        Breadcrumb("Open file", $"ext={Path.GetExtension(selected.Path).ToLowerInvariant()}");
        try
        {
            Process.Start(new ProcessStartInfo(selected.Path) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await ShowErrorAsync("Windows could not open that file.", exception);
        }
    }

    private async void Back_Click(object sender, RoutedEventArgs e) => await GoBackAsync();

    private async Task GoBackAsync()
    {
        if (ActiveTab is not { } tab || tab.Session.BackHistory.Count == 0) return;
        var session = tab.Session;
        var target = session.BackHistory[^1];
        tab.Session = session with
        {
            BackHistory = session.BackHistory.Take(session.BackHistory.Count - 1).ToArray(),
            ForwardHistory = session.ForwardHistory.Prepend(session.CurrentPath).Take(100).ToArray(),
            CurrentPath = target
        };
        await NavigateAsync(target, false);
    }

    private async void Forward_Click(object sender, RoutedEventArgs e) => await GoForwardAsync();

    private async Task GoForwardAsync()
    {
        if (ActiveTab is not { } tab || tab.Session.ForwardHistory.Count == 0) return;
        var session = tab.Session;
        var target = session.ForwardHistory[0];
        tab.Session = session with
        {
            BackHistory = session.BackHistory.Append(session.CurrentPath).TakeLast(100).ToArray(),
            ForwardHistory = session.ForwardHistory.Skip(1).ToArray(),
            CurrentPath = target
        };
        await NavigateAsync(target, false);
    }

    private async void Up_Click(object sender, RoutedEventArgs e) => await GoUpAsync();

    private async Task GoUpAsync()
    {
        if (ActiveTab is null || ActiveTab.Session.CurrentPath == SpecialLocations.ThisPc) return;
        var parent = Directory.GetParent(ActiveTab.Session.CurrentPath);
        if (parent is not null)
        {
            await NavigateAsync(parent.FullName);
        }
        else
        {
            await NavigateAsync(SpecialLocations.ThisPc);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is { } tab) await NavigateAsync(tab.Session.CurrentPath, false);
    }

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!CanMutateCurrentFolder()) return;
        Breadcrumb("New folder");
        var name = await PromptAsync("New folder", "Name", "New folder");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!IsValidLeafName(name))
        {
            await ShowErrorAsync("That folder name is not valid.", new ArgumentException(name));
            return;
        }

        try
        {
            var destination = Path.Combine(ActiveTab!.Session.CurrentPath, name);
            if (Directory.Exists(destination) || File.Exists(destination))
            {
                throw new IOException("An item with that name already exists.");
            }

            Directory.CreateDirectory(destination);
            await NavigateAsync(ActiveTab.Session.CurrentPath, false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await ShowErrorAsync("The folder could not be created.", exception);
        }
    }

    private async void NewFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string value }
            || !Enum.TryParse<NewFileKind>(value, out var kind)
            || !CanMutateCurrentFolder())
        {
            return;
        }

        Breadcrumb("New file", kind.ToString());
        var (title, defaultName, extension) = kind switch
        {
            NewFileKind.AutoHotkey => ("New AutoHotkey Script", "New AutoHotkey Script.ahk", ".ahk"),
            NewFileKind.Bitmap => ("New Bitmap image", "New Bitmap Image.bmp", ".bmp"),
            NewFileKind.Python => ("New Python Source File", "New Python File.py", ".py"),
            _ => ("New Text Document", "New Text Document.txt", ".txt")
        };
        var suggestedName = GetAvailableNewItemName(ActiveTab!.Session.CurrentPath, defaultName);
        var name = await PromptAsync(title, "Name", suggestedName);
        if (string.IsNullOrWhiteSpace(name)) return;
        if (string.IsNullOrEmpty(Path.GetExtension(name))) name += extension;
        if (!IsValidLeafName(name))
        {
            await ShowErrorAsync("That file name is not valid.", new ArgumentException(name));
            return;
        }

        try
        {
            var destination = Path.Combine(ActiveTab.Session.CurrentPath, name);
            if (Directory.Exists(destination) || File.Exists(destination))
            {
                throw new IOException("An item with that name already exists.");
            }

            if (kind == NewFileKind.Bitmap)
            {
                await File.WriteAllBytesAsync(destination, CreateBlankBitmapBytes());
            }
            else
            {
                await using var stream = new FileStream(
                    destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, useAsync: true);
                await stream.FlushAsync();
            }
            await NavigateAsync(ActiveTab.Session.CurrentPath, false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await ShowErrorAsync("The file could not be created.", exception);
        }
    }

    private async void NewShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (!CanMutateCurrentFolder()) return;
        Breadcrumb("New shortcut");
        var target = await PromptAsync("New shortcut", "Target location", string.Empty);
        if (string.IsNullOrWhiteSpace(target)) return;
        target = Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));

        var targetName = Path.GetFileNameWithoutExtension(
            target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(targetName)) targetName = "New Shortcut";
        var defaultName = GetAvailableNewItemName(
            ActiveTab!.Session.CurrentPath,
            targetName + ".lnk");
        var name = await PromptAsync("New shortcut", "Shortcut name", defaultName);
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) name += ".lnk";
        if (!IsValidLeafName(name))
        {
            await ShowErrorAsync("That shortcut name is not valid.", new ArgumentException(name));
            return;
        }

        try
        {
            var destination = Path.Combine(ActiveTab.Session.CurrentPath, name);
            if (Directory.Exists(destination) || File.Exists(destination))
            {
                throw new IOException("An item with that name already exists.");
            }
            ShellLinkService.Create(destination, target);
            await NavigateAsync(ActiveTab.Session.CurrentPath, false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException
            or System.Runtime.InteropServices.COMException or PlatformNotSupportedException)
        {
            await ShowErrorAsync("The shortcut could not be created.", exception);
        }
    }

    private static string GetAvailableNewItemName(string directory, string preferredName)
    {
        if (!File.Exists(Path.Combine(directory, preferredName))
            && !Directory.Exists(Path.Combine(directory, preferredName)))
        {
            return preferredName;
        }

        var extension = Path.GetExtension(preferredName);
        var stem = Path.GetFileNameWithoutExtension(preferredName);
        for (var suffix = 2; suffix < 10_000; suffix++)
        {
            var candidate = $"{stem} ({suffix}){extension}";
            if (!File.Exists(Path.Combine(directory, candidate))
                && !Directory.Exists(Path.Combine(directory, candidate)))
            {
                return candidate;
            }
        }

        return $"{stem} ({Guid.NewGuid():N}){extension}";
    }

    private static byte[] CreateBlankBitmapBytes() =>
    [
        0x42, 0x4D, 0x3A, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x36, 0x00, 0x00, 0x00, 0x28, 0x00,
        0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00,
        0x00, 0x00, 0x01, 0x00, 0x18, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x13, 0x0B,
        0x00, 0x00, 0x13, 0x0B, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF,
        0xFF, 0x00
    ];

    private async void Rename_Click(object sender, RoutedEventArgs e) => await RenameSelectedAsync();

    private async void Copy_Click(object sender, RoutedEventArgs e) =>
        await CopySelectionAsync(DataPackageOperation.Copy);

    private async void Cut_Click(object sender, RoutedEventArgs e) =>
        await CopySelectionAsync(DataPackageOperation.Move);

    private async Task CopySelectionAsync(DataPackageOperation operation)
    {
        var selected = FileList.SelectedItems
            .Cast<FileSystemItemViewModel>()
            .Where(item => !item.IsDrive)
            .ToArray();
        if (selected.Length == 0) return;
        Breadcrumb(operation == DataPackageOperation.Move ? "Cut" : "Copy", $"items={selected.Length}");
        try
        {
            await FileTransferService.PutOnClipboardAsync(selected, operation);
            StatusText.Text = $"{selected.Length} item{(selected.Length == 1 ? string.Empty : "s")} ready to {(operation == DataPackageOperation.Move ? "move" : "copy")}.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await ShowErrorAsync("The selected items could not be placed on the clipboard.", exception);
        }
    }

    private async void Paste_Click(object sender, RoutedEventArgs e) => await PasteAsync();

    private async void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (!CanMutateCurrentFolder()) return;
        var selected = FileList.SelectedItems
            .Cast<FileSystemItemViewModel>()
            .Where(item => !item.IsDrive)
            .ToArray();
        if (selected.Length == 0) return;

        Breadcrumb("Duplicate", $"items={selected.Length}");
        StatusText.Text = "Duplicating items…";
        try
        {
            var storageItems = await FileTransferService.GetStorageItemsAsync(selected);
            var failures = await FileTransferService.TransferAsync(
                storageItems,
                ActiveTab!.Session.CurrentPath,
                DataPackageOperation.Copy,
                CancellationToken.None);
            await NavigateAsync(ActiveTab.Session.CurrentPath, false);
            if (failures.Count > 0)
            {
                await ShowErrorAsync(
                    $"{failures.Count} item{(failures.Count == 1 ? string.Empty : "s")} could not be duplicated.",
                    new IOException(failures[0]));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await ShowErrorAsync("The selected items could not be duplicated.", exception);
        }
    }

    private async Task PasteAsync()
    {
        var tab = ActiveTab;
        if (tab is null || !CanMutateCurrentFolder()) return;
        Breadcrumb("Paste", $"to {Shape(tab.Session.CurrentPath)}");
        StatusText.Text = "Pasting…";
        try
        {
            var failures = await FileTransferService.PasteAsync(tab.Session.CurrentPath, CancellationToken.None);
            await NavigateAsync(tab.Session.CurrentPath, false);
            if (failures.Count > 0)
            {
                await ShowErrorAsync($"{failures.Count} item(s) could not be pasted.", new IOException(failures[0]));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await ShowErrorAsync("The clipboard contents could not be pasted.", exception);
        }
    }

    private async Task RenameSelectedAsync()
    {
        if (FileList.SelectedItems.Count != 1
            || FileList.SelectedItem is not FileSystemItemViewModel { IsDrive: false } selected)
        {
            return;
        }

        Breadcrumb("Rename", selected.IsFolder ? "folder" : "file");
        var name = await PromptAsync("Rename", "New name", selected.Name);
        if (string.IsNullOrWhiteSpace(name) || name == selected.Name) return;
        if (!IsValidLeafName(name))
        {
            await ShowErrorAsync("That item name is not valid.", new ArgumentException(name));
            return;
        }

        try
        {
            var destination = Path.Combine(Path.GetDirectoryName(selected.Path)!, name);
            if (selected.IsFolder) Directory.Move(selected.Path, destination);
            else File.Move(selected.Path, destination);
            await NavigateAsync(ActiveTab!.Session.CurrentPath, false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await ShowErrorAsync("The item could not be renamed.", exception);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e) => await DeleteSelectedAsync(false);

    private async Task DeleteSelectedAsync(bool permanent)
    {
        var selected = FileList.SelectedItems
            .Cast<FileSystemItemViewModel>()
            .Where(item => !item.IsDrive)
            .ToArray();
        if (selected.Length == 0) return;
        Breadcrumb("Delete", $"items={selected.Length} permanent={permanent}");
        if (permanent || _settings.ConfirmRecycleBinDeletes)
        {
            var action = permanent ? "permanently delete" : "move to the Recycle Bin";
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock
            {
                Text = $"This will {action} {selected.Length} item{(selected.Length == 1 ? string.Empty : "s")}.",
                TextWrapping = TextWrapping.Wrap
            });
            CheckBox? doNotAskAgain = null;
            if (!permanent)
            {
                doNotAskAgain = new CheckBox { Content = "Do not ask again" };
                content.Children.Add(doNotAskAgain);
            }

            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = permanent ? "Permanently delete items?" : "Move items to the Recycle Bin?",
                Content = content,
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (doNotAskAgain?.IsChecked == true)
            {
                ((App)Application.Current).ApplyRecycleBinDeleteConfirmationPreference(false);
                await SaveStateAsync();
            }
        }

        // One Shell operation on a background STA: the window stays responsive during large
        // deletes, and cancelling the Shell's error/progress UI is reported, not thrown.
        var tab = ActiveTab;
        StatusText.Text = permanent ? "Deleting items…" : "Moving items to the Recycle Bin…";
        IReadOnlyList<string> failures;
        try
        {
            failures = await ShellTransferEngine.DeleteAsync(
                selected.Select(item => item.Path), recycle: !permanent, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failures = [exception.Message];
        }

        if (tab is not null)
        {
            tab.LoadedPath = null;
            if (ReferenceEquals(tab, ActiveTab)) await NavigateAsync(tab.Session.CurrentPath, false);
        }
        else
        {
            UpdateStatusText();
        }
        if (failures.Count > 0)
        {
            await ShowErrorAsync($"{failures.Count} item(s) could not be deleted.", new IOException(failures[0]));
        }
    }

    private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is { } tab)
        {
            var target = tab.Session.CurrentPath == SpecialLocations.ThisPc
                ? "shell:MyComputerFolder"
                : tab.Session.CurrentPath;
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
        }
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var restoreTabsToggle = new ToggleSwitch
        {
            Header = "Remember open tabs",
            OffContent = "Start at Home each time",
            OnContent = "Restore tabs from the previous session",
            IsOn = _settings.RestorePreviousSession
        };
        AutomationProperties.SetName(restoreTabsToggle, "Remember open tabs");
        var description = new TextBlock
        {
            Text = "When disabled, Anz Explorer does not save open tabs when the app closes.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72,
            MaxWidth = 420
        };
        var confirmRecycleBinDeletesToggle = new ToggleSwitch
        {
            Header = "Confirm Recycle Bin deletions",
            OffContent = "Move selected items without asking",
            OnContent = "Ask before moving selected items to the Recycle Bin",
            IsOn = _settings.ConfirmRecycleBinDeletes
        };
        AutomationProperties.SetName(confirmRecycleBinDeletesToggle, "Confirm Recycle Bin deletions");
        var deleteDescription = new TextBlock
        {
            Text = "Permanent deletion always requires confirmation.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72,
            MaxWidth = 420
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(restoreTabsToggle);
        content.Children.Add(description);
        content.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(0, 4, 0, 4),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AnzBorderBrush"]
        });
        content.Children.Add(confirmRecycleBinDeletesToggle);
        content.Children.Add(deleteDescription);
        content.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(0, 4, 0, 4),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AnzBorderBrush"]
        });
        var openLogsButton = new Button { Content = "Open diagnostic logs folder" };
        openLogsButton.Click += (_, _) =>
        {
            if (App.LogDirectory is { } directory && Directory.Exists(directory))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
            }
        };
        content.Children.Add(openLogsButton);
        content.Children.Add(new TextBlock
        {
            Text = "Logs record actions and errors to help diagnose crashes. File and folder names are not recorded; crash reports include error details.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72,
            MaxWidth = 420
        });
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Settings",
            Content = content,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        Breadcrumb("Settings opened");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        Breadcrumb("Settings saved", $"restoreTabs={restoreTabsToggle.IsOn} confirmRecycle={confirmRecycleBinDeletesToggle.IsOn}");
        ((App)Application.Current).ApplyRestoreTabsPreference(restoreTabsToggle.IsOn);
        ((App)Application.Current).ApplyRecycleBinDeleteConfirmationPreference(confirmRecycleBinDeletesToggle.IsOn);
        await SaveStateAsync();
    }

    internal void ApplyRestoreTabsPreference(bool restorePreviousSession) =>
        _settings = _settings with { RestorePreviousSession = restorePreviousSession };

    internal void ApplyRecycleBinDeleteConfirmationPreference(bool confirmRecycleBinDeletes) =>
        _settings = _settings with { ConfirmRecycleBinDeletes = confirmRecycleBinDeletes };

    private void Properties_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is FileSystemItemViewModel selected)
        {
            Breadcrumb("Properties", selected.IsFolder ? "folder" : "file");
            ShellProperties.Show(selected.Path);
        }
    }

    private async void CompressZip_Click(object sender, RoutedEventArgs e)
    {
        var selection = FileList.SelectedItems
            .Cast<FileSystemItemViewModel>()
            .Where(item => !item.IsDrive)
            .ToArray();
        if (selection.Length == 0 || !CanMutateCurrentFolder()) return;
        Breadcrumb("Compress to ZIP", $"items={selection.Length}");

        StatusText.Text = selection.Length == 1
            ? $"Compressing {selection[0].Name}…"
            : $"Compressing {selection.Length} items…";
        try
        {
            var sources = selection.Select(item => new FileTransferSource(
                item.Path,
                item.Name,
                item.IsFolder));
            var archivePath = await ZipArchiveService.CreateAsync(
                sources,
                ActiveTab!.Session.CurrentPath,
                CancellationToken.None);
            await NavigateAsync(ActiveTab.Session.CurrentPath, false);
            StatusText.Text = $"Created {Path.GetFileName(archivePath)}";
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            await ShowErrorAsync("The ZIP archive could not be created.", exception);
        }
    }

    private void FileList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var position = e.GetPosition(FileList);
        var showOptions = new FlyoutShowOptions
        {
            Position = position,
            Placement = FlyoutPlacementMode.Auto
        };
        var hitItem = GetFileItemAtPoint(position);
        Breadcrumb("Context menu", hitItem is null ? "background" : hitItem.IsFolder ? "folder" : "file");
        if (hitItem is { } item)
        {
            if (!FileList.SelectedItems.Contains(item))
            {
                FileList.SelectedItems.Clear();
                FileList.SelectedItem = item;
            }

            e.Handled = true;
            FileContextFlyout.ShowAt(FileList, showOptions);
            return;
        }

        FileList.SelectedItems.Clear();
        e.Handled = true;
        BackgroundContextFlyout.ShowAt(FileList, showOptions);
    }

    private FileSystemItemViewModel? GetFileItemAtPoint(Windows.Foundation.Point position)
    {
        foreach (var item in Items)
        {
            if (FileList.ContainerFromItem(item) is not ListViewItem container
                || container.Visibility != Visibility.Visible
                || container.ActualWidth <= 0
                || container.ActualHeight <= 0)
            {
                continue;
            }

            try
            {
                var origin = container.TransformToVisual(FileList)
                    .TransformPoint(new Windows.Foundation.Point(0, 0));
                if (position.X >= origin.X
                    && position.X <= origin.X + container.ActualWidth
                    && position.Y >= origin.Y
                    && position.Y <= origin.Y + container.ActualHeight)
                {
                    return item;
                }
            }
            catch (ArgumentException)
            {
                // Virtualization can detach a row between lookup and transform.
            }
        }

        return null;
    }

    private void BackgroundContextMenu_Opening(object sender, object e)
    {
        var canMutate = CanMutateCurrentFolder();
        BackgroundNewMenu.IsEnabled = canMutate;
        BackgroundPasteMenuItem.IsEnabled = canMutate && ClipboardContainsStorageItems();
        var hasFolder = ActiveTab is { } tab
            && tab.Session.CurrentPath != SpecialLocations.ThisPc
            && Directory.Exists(tab.Session.CurrentPath);
        BackgroundOpenTerminalMenuItem.IsEnabled = hasFolder;
        BackgroundPropertiesMenuItem.IsEnabled = hasFolder;

        UpdateSortMenuChecks();
        UpdateViewMenuChecks();
    }

    private void SortMenu_Opening(object sender, object e) => UpdateSortMenuChecks();

    private void ViewMenu_Opening(object sender, object e) => UpdateViewMenuChecks();

    private void UpdateSortMenuChecks()
    {
        var sortProperty = ActiveTab?.Session.SortProperty;
        var ascending = ActiveTab?.Session.SortDirection == SortDirection.Ascending;
        var descending = ActiveTab?.Session.SortDirection == SortDirection.Descending;

        foreach (var item in new[] { BackgroundSortNameMenuItem, CommandSortNameMenuItem })
            item.IsChecked = string.Equals(sortProperty, nameof(FileSystemItemViewModel.Name), StringComparison.Ordinal);
        foreach (var item in new[] { BackgroundSortModifiedMenuItem, CommandSortModifiedMenuItem })
            item.IsChecked = string.Equals(sortProperty, nameof(FileSystemItemViewModel.Modified), StringComparison.Ordinal);
        foreach (var item in new[] { BackgroundSortTypeMenuItem, CommandSortTypeMenuItem })
            item.IsChecked = string.Equals(sortProperty, nameof(FileSystemItemViewModel.Type), StringComparison.Ordinal);
        foreach (var item in new[] { BackgroundSortSizeMenuItem, CommandSortSizeMenuItem })
            item.IsChecked = string.Equals(sortProperty, nameof(FileSystemItemViewModel.LogicalBytes), StringComparison.Ordinal);
        foreach (var item in new[] { BackgroundSortAscendingMenuItem, CommandSortAscendingMenuItem })
            item.IsChecked = ascending;
        foreach (var item in new[] { BackgroundSortDescendingMenuItem, CommandSortDescendingMenuItem })
            item.IsChecked = descending;
    }

    private void UpdateViewMenuChecks()
    {
        var mode = ActiveTab?.Session.ViewMode ?? FileViewMode.Details;
        if (mode == FileViewMode.Icons) mode = FileViewMode.MediumIcons;
        SetViewModeChecked(FileViewMode.ExtraLargeIcons, ViewExtraLargeMenuItem, BackgroundViewExtraLargeMenuItem);
        SetViewModeChecked(FileViewMode.LargeIcons, ViewLargeMenuItem, BackgroundViewLargeMenuItem);
        SetViewModeChecked(FileViewMode.MediumIcons, ViewMediumMenuItem, BackgroundViewMediumMenuItem);
        SetViewModeChecked(FileViewMode.SmallIcons, ViewSmallMenuItem, BackgroundViewSmallMenuItem);
        SetViewModeChecked(FileViewMode.List, ViewListMenuItem, BackgroundViewListMenuItem);
        SetViewModeChecked(FileViewMode.Details, ViewDetailsMenuItem, BackgroundViewDetailsMenuItem);
        SetViewModeChecked(FileViewMode.Tiles, ViewTilesMenuItem, BackgroundViewTilesMenuItem);
        SetViewModeChecked(FileViewMode.Content, ViewContentMenuItem, BackgroundViewContentMenuItem);

        void SetViewModeChecked(FileViewMode candidate, params ToggleMenuFlyoutItem[] items)
        {
            foreach (var item in items) item.IsChecked = mode == candidate;
        }
    }

    private async void ViewMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: string value }
            || !Enum.TryParse<FileViewMode>(value, out var mode)
            || ActiveTab is not { } tab)
        {
            return;
        }

        Breadcrumb("View mode", mode.ToString());
        tab.Session = tab.Session with { ViewMode = mode, ScrollOffset = 0 };
        tab.AppliedViewMode = null;
        ApplyViewMode(tab);
        UpdateViewMenuChecks();
        await SaveStateAsync();
    }

    private async void ZoomIn_Click(object sender, RoutedEventArgs e) =>
        await ChangeDisplayScaleAsync(DisplayScaleStep);

    private async void ZoomOut_Click(object sender, RoutedEventArgs e) =>
        await ChangeDisplayScaleAsync(-DisplayScaleStep);

    private async Task ChangeDisplayScaleAsync(double change)
    {
        var requested = Math.Round((DisplayScale.Scale + change) * 10, MidpointRounding.AwayFromZero) / 10;
        DisplayScale.Scale = Math.Clamp(requested, MinimumDisplayScale, MaximumDisplayScale);
        Breadcrumb("Zoom", $"{DisplayScale.Scale:0.0}");
        _settings = _settings with { DisplayScale = DisplayScale.Scale };
        UpdateZoomCommandState();
        await SaveStateAsync();
    }

    private void UpdateZoomCommandState()
    {
        ZoomOutButton.IsEnabled = DisplayScale.Scale > MinimumDisplayScale + 0.001;
        ZoomInButton.IsEnabled = DisplayScale.Scale < MaximumDisplayScale - 0.001;
        ToolTipService.SetToolTip(
            ZoomOutButton,
            $"Zoom out (Ctrl+-) — {DisplayScale.Scale:P0}");
        ToolTipService.SetToolTip(
            ZoomInButton,
            $"Zoom in (Ctrl+=) — {DisplayScale.Scale:P0}");
    }

    private static bool ClipboardContainsStorageItems()
    {
        try
        {
            return Clipboard.GetContent().Contains(StandardDataFormats.StorageItems);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async void BackgroundSortProperty_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: string property } || ActiveTab is not { } tab) return;
        await SetBackgroundSortAsync(property, tab.Session.SortDirection);
    }

    private async void BackgroundSortDirection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: string value }
            || !Enum.TryParse<SortDirection>(value, out var direction)
            || ActiveTab is not { } tab)
        {
            return;
        }

        await SetBackgroundSortAsync(tab.Session.SortProperty, direction);
    }

    private async Task SetBackgroundSortAsync(string property, SortDirection direction)
    {
        if (ActiveTab is not { } tab) return;
        tab.Session = tab.Session with { SortProperty = property, SortDirection = direction };
        Breadcrumb("Sort (menu)", $"{property} {direction} items={tab.Items.Count}");
        UpdateSortIndicators();
        ApplyCurrentSort();
        await SaveStateAsync();
    }

    private async void BackgroundOpenTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is not { } tab || !Directory.Exists(tab.Session.CurrentPath)) return;
        try
        {
            var startInfo = new ProcessStartInfo("wt.exe") { UseShellExecute = true };
            startInfo.ArgumentList.Add("-d");
            startInfo.ArgumentList.Add(tab.Session.CurrentPath);
            Process.Start(startInfo);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await ShowErrorAsync("Windows Terminal could not be opened.", exception);
        }
    }

    private void BackgroundProperties_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is { } tab && Directory.Exists(tab.Session.CurrentPath))
        {
            ShellProperties.Show(tab.Session.CurrentPath);
        }
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateStatusText();
        UpdateCommandState();
    }

    private void FileContextMenu_Opening(object sender, object e)
    {
        var selected = FileList.SelectedItem as FileSystemItemViewModel;
        var selection = FileList.SelectedItems.Cast<FileSystemItemViewModel>().ToArray();
        var hasSelection = selection.Length > 0;
        var containsDrive = selection.Any(item => item.IsDrive);
        var exactlyOne = selection.Length == 1;
        OpenMenuItem.IsEnabled = exactlyOne;
        OpenNewTabMenuItem.Visibility = selected is { IsFolder: true } && exactlyOne
            ? Visibility.Visible
            : Visibility.Collapsed;
        PopulateOpenWithMenu(selected, exactlyOne);
        CopyMenuItem.IsEnabled = hasSelection && !containsDrive;
        CutMenuItem.IsEnabled = hasSelection && !containsDrive;
        DuplicateMenuItem.IsEnabled = hasSelection && !containsDrive && CanMutateCurrentFolder();
        RenameMenuItem.IsEnabled = exactlyOne && !containsDrive;
        DeleteMenuItem.IsEnabled = hasSelection && !containsDrive;
        PropertiesMenuItem.IsEnabled = exactlyOne;
        CompressToMenu.IsEnabled = hasSelection && !containsDrive && CanMutateCurrentFolder();
        CompressZipMenuItem.IsEnabled = CompressToMenu.IsEnabled;
        var canPin = selected is { IsFolder: true, IsDrive: false };
        PinSidebarMenuItem.Visibility = canPin ? Visibility.Visible : Visibility.Collapsed;
        PinSidebarMenuItem.IsEnabled = canPin && !IsPinnedPath(selected!.Path);
        PinSidebarMenuItem.Text = canPin && IsPinnedPath(selected!.Path)
            ? "Pinned to sidebar"
            : "Pin to sidebar";
    }

    private bool CanMutateCurrentFolder() => ActiveTab is { } tab
        && tab.Session.CurrentPath != SpecialLocations.ThisPc
        && Directory.Exists(tab.Session.CurrentPath);

    private static bool IsValidLeafName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && name is not "." and not ".."
        && string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal)
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private void UpdateCommandState()
    {
        var selection = FileList.SelectedItems.Cast<FileSystemItemViewModel>().ToArray();
        var containsDrive = selection.Any(item => item.IsDrive);
        var canOperateOnSelection = selection.Length > 0 && !containsDrive;
        NewFolderButton.IsEnabled = CanMutateCurrentFolder();
        PasteButton.IsEnabled = CanMutateCurrentFolder();
        CopyButton.IsEnabled = canOperateOnSelection;
        CutButton.IsEnabled = canOperateOnSelection;
        RenameButton.IsEnabled = selection.Length == 1 && !containsDrive;
        DeleteButton.IsEnabled = canOperateOnSelection;
    }

    private async void PinSidebar_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not FileSystemItemViewModel { IsFolder: true, IsDrive: false } selected
            || IsPinnedPath(selected.Path))
        {
            return;
        }

        Breadcrumb("Pin to sidebar", Shape(selected.Path));
        EnsurePinnedGroup();
        var group = _sidebarGroups[0];
        var items = group.Items.ToList();
        items.Add(new SidebarItem(
            Guid.NewGuid(),
            selected.Name,
            selected.Path,
            selected.Path.StartsWith("\\\\", StringComparison.Ordinal)
                ? SidebarTargetType.Network
                : SidebarTargetType.FileSystemPath,
            "Folder",
            items.Count));
        _sidebarGroups[0] = group with { Items = items };
        CommitSidebarChange();
        await SaveStateAsync();
    }

    private bool IsPinnedPath(string path) => _sidebarGroups
        .SelectMany(group => group.Items)
        .Any(item => string.Equals(item.Target, path, StringComparison.OrdinalIgnoreCase));

    private void EnsurePinnedGroup()
    {
        if (_sidebarGroups.Count == 0)
        {
            _sidebarGroups.Add(new SidebarGroup(Guid.NewGuid(), "Pinned", 0, false, []));
        }
    }

    private async void SidebarOpen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is SidebarItem item)
        {
            await NavigateAsync(item.Target);
        }
    }

    private async void SidebarOpenNewTab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is SidebarItem item)
        {
            AddTab(item.Target, true);
            await SaveStateAsync();
        }
    }

    private void SidebarOpenNewWindow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is not SidebarItem item) return;

        // Open in-process so the window shares sidebar/settings state with this one.
        // (Launching a second process also mangled quoted drive roots such as "C:\".)
        ((App)Application.Current).CreateWindow(item.Target);
    }

    private static void SidebarCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is not SidebarItem item) return;
        var data = new DataPackage();
        data.SetText(item.Target == SpecialLocations.ThisPc ? "This PC" : item.Target);
        Clipboard.SetContent(data);
        Clipboard.Flush();
    }

    private static void SidebarOpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is not SidebarItem item) return;
        var target = item.Target == SpecialLocations.ThisPc ? "shell:MyComputerFolder" : item.Target;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
    }

    private static void SidebarProperties_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is SidebarItem item
            && item.Target != SpecialLocations.ThisPc)
        {
            ShellProperties.Show(item.Target);
        }
    }

    private async void SidebarUnpin_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is not SidebarItem item) return;
        Breadcrumb("Unpin from sidebar");
        MutateSidebarItem(item.Id, (_, items, index) => items.RemoveAt(index));
        CommitSidebarChange();
        await SaveStateAsync();
    }

    private void CommitSidebarChange()
    {
        RebuildSidebar();
        ((App)Application.Current).NotifySidebarChanged(this);
    }

    internal void OnSharedSidebarChanged()
    {
        if (_initializing) return;
        RebuildSidebar();
    }

    private void UpdateStatusText()
    {
        if (ActiveTab?.Session.CurrentPath == SpecialLocations.ThisPc)
        {
            StatusText.Text = $"{Items.Count:N0} available drive{(Items.Count == 1 ? string.Empty : "s")}";
        }
        else
        {
            StatusText.Text = $"{Items.Count:N0} item{(Items.Count == 1 ? string.Empty : "s")}";
        }

        if (FileList.SelectedItems.Count > 0)
        {
            StatusText.Text += $"   |   {FileList.SelectedItems.Count:N0} selected";
        }
    }

    private void MutateSidebarItem(Guid itemId, Action<SidebarGroup, List<SidebarItem>, int> mutation)
    {
        for (var groupIndex = 0; groupIndex < _sidebarGroups.Count; groupIndex++)
        {
            var group = _sidebarGroups[groupIndex];
            var items = group.Items.OrderBy(item => item.Order).ToList();
            var itemIndex = items.FindIndex(item => item.Id == itemId);
            if (itemIndex < 0) continue;
            mutation(group, items, itemIndex);
            _sidebarGroups[groupIndex] = group with
            {
                Items = items.Select((item, order) => item with { Order = order }).ToArray()
            };
            return;
        }
    }

    private async void FileList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var control = IsKeyDown(VirtualKey.Control);
        var alt = IsKeyDown(VirtualKey.Menu);
        var shift = IsKeyDown(VirtualKey.Shift);
        // Logged before acting, so a crash inside the command still leaves the key in the log.
        if (control || alt || e.Key is VirtualKey.F2 or VirtualKey.F5 or VirtualKey.Delete or VirtualKey.Enter)
        {
            Breadcrumb("Key", $"{(control ? "Ctrl+" : "")}{(alt ? "Alt+" : "")}{(shift ? "Shift+" : "")}{e.Key}");
        }

        if (control && e.Key == VirtualKey.L)
        {
            ShowAddressEditor();
            e.Handled = true;
        }
        else if (control && (e.Key == OemPlusKey || e.Key == VirtualKey.Add))
        {
            await ChangeDisplayScaleAsync(DisplayScaleStep);
            e.Handled = true;
        }
        else if (control && (e.Key == OemMinusKey || e.Key == VirtualKey.Subtract))
        {
            await ChangeDisplayScaleAsync(-DisplayScaleStep);
            e.Handled = true;
        }
        else if (control && e.Key == VirtualKey.C)
        {
            await CopySelectionAsync(DataPackageOperation.Copy);
            e.Handled = true;
        }
        else if (control && e.Key == VirtualKey.X)
        {
            await CopySelectionAsync(DataPackageOperation.Move);
            e.Handled = true;
        }
        else if (control && e.Key == VirtualKey.V)
        {
            await PasteAsync();
            e.Handled = true;
        }
        else if (control && e.Key == VirtualKey.T)
        {
            AddTab(ResolveHomePath(), true);
            e.Handled = true;
        }
        else if (control && shift && e.Key == VirtualKey.N)
        {
            NewFolder_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (control && e.Key == VirtualKey.W && Tabs.SelectedItem is TabViewItem selected)
        {
            if (Tabs.TabItems.Count == 1)
            {
                e.Handled = true;
                Close();
                return;
            }
            Tabs.TabItems.Remove(selected);
            if (selected.Tag is TabRuntime runtime) DestroyFileList(runtime);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.F2)
        {
            await RenameSelectedAsync();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Delete)
        {
            await DeleteSelectedAsync(shift);
            e.Handled = true;
        }
        else if (alt && e.Key == VirtualKey.Left)
        {
            await GoBackAsync();
            e.Handled = true;
        }
        else if (alt && e.Key == VirtualKey.Right)
        {
            await GoForwardAsync();
            e.Handled = true;
        }
        else if (alt && e.Key == VirtualKey.Up)
        {
            await GoUpAsync();
            e.Handled = true;
        }
        else if (alt && e.Key == VirtualKey.Enter)
        {
            Properties_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.F5)
        {
            if (ActiveTab is { } tab) await NavigateAsync(tab.Session.CurrentPath, false);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter)
        {
            await OpenSelectedAsync();
            e.Handled = true;
        }
    }

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private async Task<string?> PromptAsync(string title, string header, string defaultValue)
    {
        var input = new TextBox { Header = header, Text = defaultValue, SelectionStart = 0, SelectionLength = defaultValue.Length };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = input,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? input.Text.Trim() : null;
    }

    private async Task ShowErrorAsync(string title, Exception exception)
    {
        DiagnosticLog.Warn("UI", $"W{DiagnosticId} Error dialog: {title}", exception);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = exception.Message,
            CloseButtonText = "OK"
        };
        await dialog.ShowAsync();
    }

    private async Task SaveStateAsync()
    {
        await _initializationTask;
        if (!_settingsLoaded) return;
        var tabs = _settings.RestorePreviousSession
            ? ((App)Application.Current).GetOpenTabSessions()
            : [];
        _settings = _settings with
        {
            SidebarGroups = _sidebarGroups.ToArray(),
            Tabs = tabs,
            ActiveTabId = _settings.RestorePreviousSession ? ActiveTab?.Session.Id : null,
            DetailsColumnWidths = ColumnLayout.ToContract(),
            DisplayScale = DisplayScale.Scale,
            FolderSortPreferences = ((App)Application.Current).FolderSortPreferences.Values.ToArray()
        };
        try
        {
            await _settingsStore.SaveAsync(_settings);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException
                or InvalidDataException or NotSupportedException)
        {
            // SaveStateAsync is awaited from many async void handlers. A transient lock
            // (antivirus, sync tools) must not take down the app; the next save retries.
            DiagnosticLog.Warn("Settings", "Save failed", exception);
            StatusText.Text = "Settings could not be saved. They will be saved on the next change.";
        }
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        Breadcrumb("Closing", $"tabs={Tabs.TabItems.Count} saveState={!_suppressStateSaveOnClose}");
        _driveRefreshTimer.Stop();
        _navigationCancellation?.Cancel();
        _searchCancellation?.Cancel();
        // SaveStateAsync handles its own persistence failures.
        if (!_suppressStateSaveOnClose) await SaveStateAsync();
    }

    private static string GetTabTitle(string path)
    {
        if (path == SpecialLocations.ThisPc) return "This PC";
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private async void DriveRefreshTimer_Tick(object? sender, object e)
    {
        if (_driveRefreshRunning) return;
        _driveRefreshRunning = true;
        try
        {
            var refreshed = await Task.Run(DriveService.GetAvailableDrives);
            var previousSignature = string.Join('|', _drives.Select(DriveSignature));
            var refreshedSignature = string.Join('|', refreshed.Select(DriveSignature));
            if (previousSignature == refreshedSignature) return;
            Breadcrumb("Drives changed", $"count={refreshed.Count} ready={refreshed.Count(drive => drive.IsReady)}");
            _drives = refreshed;
            RebuildSidebar();
            if (ActiveTab?.Session.CurrentPath == SpecialLocations.ThisPc)
            {
                await NavigateAsync(SpecialLocations.ThisPc, false);
            }
        }
        finally
        {
            _driveRefreshRunning = false;
        }
    }

    private static string DriveSignature(DriveDescriptor drive) =>
        $"{drive.RootPath}|{drive.DisplayName}|{drive.CapacityText}|{drive.IsReady}";
}
