using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using ChangJing.App.Core.Collections;
using ChangJing.App.Core.FileSystem;
using ChangJing.App.Core.Grouping;
using ChangJing.App.Core.LongPath;
using ChangJing.App.Core.Operations;
using ChangJing.App.Core.RecycleBin;
using ChangJing.App.Core.ShellNew;
using ChangJing.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CustomWin.Utils.Logs;

namespace ChangJing.App.ViewModels;

public enum FileSortColumn
{
    Name,

    DateModified,

    Type,

    Size,

    Folder,
}


public sealed partial class MainViewModel : ObservableObject
{
    public const string ApplicationTitle = "畅径";

    public const string ThisPcDisplayName = "此电脑";

    public const string ThisPcToken = "::ThisPC";

    public const string RecycleBinDisplayName = "回收站";

    public const string RecycleBinToken = "::RecycleBin";


    public const double MinPreviewPaneWidth = 160;

    private const int StreamingBatchSize = 200;

    private const int FirstFlushThreshold = StreamingBatchSize;

    private const int FlushThreshold = 1000;

    public const int MaxMultiOpenCount = 10;

    private readonly Dispatcher _dispatcher;
    private readonly IOperationCoordinator _coordinator;
    private readonly IRecycleBinCoordinator _recycleBin;
    private readonly AppClipboard _clipboard;
    private readonly RecentTargetStore _recentTargets;
    private readonly FolderViewStore? _folderViews;
    private readonly BatchObservableCollection<FileItemViewModel> _items = new();
    private readonly ObservableCollection<RecycleBinItemViewModel> _recycleBinItems = new();
    private readonly List<RecycleBinItemViewModel> _recycleBinSelectedItems = new();
    private readonly List<string> _backStack = new();
    private readonly List<string> _forwardStack = new();
    private readonly List<FileItemViewModel> _selectedItems = new();

    private CancellationTokenSource? _loadCts;
    private int _loadGeneration;
    private int _problematicCount;
    private bool _showHidden;
    private bool _showSystem;
    private FileSortColumn _sortColumn = FileSortColumn.Name;
    private bool _sortDescending;
    private string _currentLocation = ThisPcToken;
    private NavigationNodeViewModel? _selectedTreeNode;
    private FileItemViewModel? _renamingItem;

    // 后台探测完成前用户若已手动导航，不再用启动目标覆盖
    private bool _startupNavigationPending;

    private FileSortColumn _searchSortColumn = FileSortColumn.Name;
    private bool _searchSortDescending;

    private RecycleBinSortColumn _recycleBinSortColumn = RecycleBinSortColumn.DeletedDate;
    private bool _recycleBinSortDescending = true;

    private int _recycleBinLoadGeneration;


    private bool _restoringFolderView;

    private HashSet<string>? _selectionToRestore;

    private string? _selectAfterLoad;

    // 递归搜索状态

    private CancellationTokenSource? _searchCts;

    private string _searchRootDisplayPath = string.Empty;

    private string _searchKeyword = string.Empty;

    // 后台线程累积，界面线程合并入列
    private readonly object _searchLock = new();

    private List<FileEntry>? _searchPending;
    private bool _searchFlushScheduled;

    private int _searchSkippedCount;
    private int _searchSkippedLinkCount;

    // 后台线程写，界面线程读
    private volatile int _searchFirstBatchMs = -1;

    private long _searchStartedTimestamp;

    // 新建类型 / 管理员权限

    private readonly List<NewMenuItem> _newItems = new();

    private bool _shellNewLoadStarted;

    private ElevationAnswer? _elevationApplyToAll;

    public event EventHandler<ElevationRequestEventArgs>? ElevationRequested;

    public enum ElevationAnswer
    {
        Relaunch,

        Skip,

        Cancel,
    }

    public bool IsElevated => ElevationService.IsElevated;

    public bool ShowElevationButton => !IsElevated;

    public ElevationAnswer RequestElevation(string folderDisplayPath, bool allowApplyToAll)
    {
        if (_elevationApplyToAll is { } cached)
        {
            return cached;
        }

        var handler = ElevationRequested;
        if (handler is null)
        {
            // 无界面（自动化 / 无窗口）：默认跳过，不阻断流程
            return ElevationAnswer.Skip;
        }

        var args = new ElevationRequestEventArgs(folderDisplayPath, allowApplyToAll);
        handler(this, args);
        return args.Answer;
    }

    public void SetElevationAnswer(ElevationRequestEventArgs args, ElevationAnswer answer, bool applyToAll)
    {
        args.Answer = answer;
        if (applyToAll)
        {
            _elevationApplyToAll = answer;
        }
    }

    public void ResetElevationApplyToAll() => _elevationApplyToAll = null;

    /// <summary>仅验证用：把当前状态置为「无权限 / 有权限」</summary>
    public void SetAccessDeniedForTest(bool value) => IsAccessDenied = value;

    /// <summary>仅验证用：直接设置当前文件夹</summary>
    public void SetCurrentLocationForTest(string location)
    {
        _currentLocation = location;
        IsThisPcView = false;
        IsRecycleBinView = false;
        IsSearchResultView = false;
    }

    public IReadOnlyList<NewMenuItem> NewItems => _newItems;

    [ObservableProperty]
    private bool _isNewItemsLoaded;

    /// <summary>启动后后台读取一次注册表 ShellNew 类型</summary>
    public void EnsureNewItemsLoaded()
    {
        if (_shellNewLoadStarted)
        {
            return;
        }

        _shellNewLoadStarted = true;

        // 文件夹 / 快捷方式恒在最前，分隔线之后是注册表登记的类型
        _newItems.Clear();
        _newItems.Add(NewMenuItem.Folder());
        _newItems.Add(NewMenuItem.Shortcut());

        _ = Task.Run(() =>
        {
            try
            {
                IReadOnlyList<ShellNewEntry> entries = ShellNewRegistry.Load().Entries;
                _dispatcher.InvokeAsync(() =>
                {
                    foreach (ShellNewEntry entry in entries)
                    {
                        _newItems.Add(NewMenuItem.FromShellNew(entry));
                    }

                    IsNewItemsLoaded = true;
                    OnPropertyChanged(nameof(NewItems));
                    LogManager.Info(typeof(MainViewModel), $"新建菜单：注册表 ShellNew 类型 {entries.Count} 个。");
                });
            }
            catch (Exception ex)
            {
                LogManager.Warning(typeof(MainViewModel), $"读取注册表 ShellNew 类型失败：{ex.Message}");
                _dispatcher.InvokeAsync(() => IsNewItemsLoaded = true);
            }
        });
    }


    public MainViewModel()
        : this(Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher)
    {
    }

    public MainViewModel(Dispatcher dispatcher)
        : this(
            dispatcher,
            new OperationCoordinator(new FileTransferService(), dispatcher, AppClipboard.Current),
            new RecycleBinStore())
    {
    }

    public MainViewModel(Dispatcher dispatcher, IOperationCoordinator coordinator)
        : this(dispatcher, coordinator, new RecycleBinStore())
    {
    }

    public MainViewModel(
        Dispatcher dispatcher,
        IOperationCoordinator coordinator,
        Core.RecycleBin.RecycleBinStore recycleBinStore,
        IRecycleBinCoordinator? recycleBinCoordinator = null)
        : this(
            dispatcher,
            coordinator,
            new RecentTargetStore(new Core.Settings.SettingsService()),
            folderViews: null,
            recycleBinCoordinator ?? new RecycleBinCoordinator(
                recycleBinStore ?? throw new ArgumentNullException(nameof(recycleBinStore)),
                coordinator,
                confirmation: null))
    {
    }

    public MainViewModel(Dispatcher dispatcher, RecentTargetStore recentTargets, FolderViewStore? folderViews = null)
        : this(
            dispatcher,
            new OperationCoordinator(new FileTransferService(), dispatcher, AppClipboard.Current),
            recentTargets,
            folderViews)
    {
    }

    public MainViewModel(
        Dispatcher dispatcher,
        IOperationCoordinator coordinator,
        RecentTargetStore? recentTargets,
        FolderViewStore? folderViews = null,
        IRecycleBinCoordinator? recycleBinCoordinator = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _recycleBin = recycleBinCoordinator
            ?? new RecycleBinCoordinator(new Core.RecycleBin.RecycleBinStore(), coordinator, confirmation: null);
        _clipboard = AppClipboard.Current;
        _recentTargets = recentTargets ?? new RecentTargetStore(new Core.Settings.SettingsService());
        _folderViews = folderViews;

        _clipboard.Changed += OnClipboardChanged;
        _coordinator.OperationCompleted += OnOperationCompleted;

        ItemsView = CollectionViewSource.GetDefaultView(_items);
        ItemsView.Filter = FilterItem;

        NavigationRoots = new ObservableCollection<NavigationNodeViewModel>();
    }

    // 视图交互事件

    public event EventHandler<ItemFocusEventArgs>? ItemFocusRequested;

    public event EventHandler<UserMessageEventArgs>? UserMessageRequested;

    public event EventHandler<CopyMoveTargetRequestEventArgs>? CopyMoveTargetRequested;

    public IOperationCoordinator Coordinator => _coordinator;

    public string Title => WindowTitle;

    /// <summary>标题栏 / 任务栏标题；完整名称、不省略</summary>
    public string WindowTitle => TitleTextFormat.Compose(TitleLocationName, IsElevated);


    public ICollectionView ItemsView { get; }

    public IReadOnlyList<FileItemViewModel> Items => _items;

    public ObservableCollection<NavigationNodeViewModel> NavigationRoots { get; }

    public ObservableCollection<RecycleBinItemViewModel> RecycleBinItems => _recycleBinItems;

    // 「此电脑」页面

    public ObservableCollection<ThisPcFolderViewModel> ThisPcFolders { get; } = new();

    public ObservableCollection<ThisPcDriveViewModel> ThisPcDrives { get; } = new();

    public string ThisPcFolderGroupHeader => $"文件夹 ({ThisPcFolders.Count})";

    public string ThisPcDriveGroupHeader => $"设备和驱动器 ({ThisPcDrives.Count})";

    [ObservableProperty]
    private bool _isThisPcFolderGroupExpanded = true;

    [ObservableProperty]
    private bool _isThisPcDriveGroupExpanded = true;

    // 绑定属性


    [ObservableProperty]
    private string _currentLocationDisplay = ThisPcToken;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFileListView))]
    [NotifyPropertyChangedFor(nameof(ShowIconListView))]
    [NotifyPropertyChangedFor(nameof(ShowThisPcPage))]
    [NotifyPropertyChangedFor(nameof(ShowSearchFolderColumn))]
    [NotifyPropertyChangedFor(nameof(ShowNewButton))]
    [NotifyPropertyChangedFor(nameof(SearchHighlightKeyword))]
    private bool _isSearchResultView;

    public bool ShowSearchFolderColumn => IsSearchResultView;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchMessage))]
    private string _searchMessage = string.Empty;

    public bool HasSearchMessage => !string.IsNullOrEmpty(SearchMessage);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancelSearch))]
    [NotifyPropertyChangedFor(nameof(CanPauseSearch))]
    private bool _isSearching;

    public bool CanCancelSearch => IsSearching;
    public bool HasActiveSearch => _searchCts is not null;

    public bool CanStartRecursiveSearch()
        => !IsThisPcView && !IsRecycleBinView && !string.IsNullOrWhiteSpace(SearchText);

    public int SearchResultCount => _items.Count;

    /// <summary>搜索首屏耗时，单位毫秒</summary>
    public int SearchFirstBatchMilliseconds => _searchFirstBatchMs;

    public int SearchSkippedDirectoryCount => _searchSkippedCount;

    public int SearchSkippedLinkCount => _searchSkippedLinkCount;

    public string SearchFolderColumnHeader => "所在文件夹";

    public string SearchLocationDisplay
    {
        get
        {
            string folderName;
            try
            {
                folderName = PathHelper.GetName(PathHelper.Normalize(_searchRootDisplayPath));
            }
            catch
            {
                folderName = _searchRootDisplayPath;
            }

            if (string.IsNullOrEmpty(folderName))
            {
                folderName = _searchRootDisplayPath;
            }

            return $"“{_searchKeyword}”的搜索结果 - {folderName}";
        }
    }

    public string SearchLocationText => IsSearchResultView ? SearchLocationDisplay : string.Empty;

    [ObservableProperty]
    private string _statusText = "共 0 个项目";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasListMessage))]
    [NotifyPropertyChangedFor(nameof(ListAreaMessage))]
    [NotifyPropertyChangedFor(nameof(HasListAreaMessage))]
    private string _listMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmptyHint))]
    [NotifyPropertyChangedFor(nameof(ListAreaMessage))]
    [NotifyPropertyChangedFor(nameof(HasListAreaMessage))]
    private string _emptyHint = string.Empty;

    public bool HasEmptyHint => !string.IsNullOrEmpty(EmptyHint);

    public string ListAreaMessage => !string.IsNullOrEmpty(ListMessage)
        ? ListMessage
        : !string.IsNullOrEmpty(LocationUnavailableMessage) ? LocationUnavailableMessage : EmptyHint;

    public bool HasListAreaMessage => !string.IsNullOrEmpty(ListAreaMessage);

    // 异常场景（无权限 / 位置不可用）

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowReturnUpButton))]
    private bool _isLocationUnavailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListAreaMessage))]
    [NotifyPropertyChangedFor(nameof(HasListAreaMessage))]
    private string _locationUnavailableMessage = string.Empty;

    public bool ShowReturnUpButton => IsLocationUnavailable || IsAccessDenied;

    public bool ShowElevationRelaunchButton => IsAccessDenied && ShowElevationButton;

    [RelayCommand]
    private void RelaunchElevated() => RequestRelaunchElevated(_currentLocation);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowReturnUpButton))]
    [NotifyPropertyChangedFor(nameof(ShowElevationRelaunchButton))]
    private bool _isAccessDenied;


    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNewButton))]
    [NotifyPropertyChangedFor(nameof(ShowRepairPathsButton))]
    private bool _isThisPcView = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNewButton))]
    [NotifyPropertyChangedFor(nameof(ShowRepairPathsButton))]
    private bool _isRecycleBinView;

    private bool _recycleBinLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecycleBinMessage))]
    private string _recycleBinMessage = string.Empty;

    public bool HasRecycleBinMessage => !string.IsNullOrEmpty(RecycleBinMessage);

    [ObservableProperty]
    private bool _isRecycleBinLoading;


    public bool HasListMessage => !string.IsNullOrEmpty(ListMessage);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewPaneColumnWidth))]
    [NotifyPropertyChangedFor(nameof(PreviewSplitterWidth))]
    private bool _isPreviewPaneOpen;

    [ObservableProperty]
    private double _navigationWidth;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewPaneColumnWidth))]
    private double _previewPaneWidth;

    public double PreviewPaneColumnWidth
    {
        get => IsPreviewPaneOpen ? Math.Max(PreviewPaneWidth, MinPreviewPaneWidth) : 0d;
        set
        {
            if (value <= 0 || value == PreviewPaneWidth)
            {
                return;
            }

            PreviewPaneWidth = value;
        }
    }

    /// <summary>预览窗格分隔条宽度，单位像素</summary>
    public double PreviewSplitterWidth => IsPreviewPaneOpen ? 5d : 0d;

    // 列头

    public string NameColumnHeader => BuildColumnHeader("名称", FileSortColumn.Name);

    public string DateColumnHeader => BuildColumnHeader("修改日期", FileSortColumn.DateModified);

    public string TypeColumnHeader => BuildColumnHeader("类型", FileSortColumn.Type);

    public string SizeColumnHeader => BuildColumnHeader("大小", FileSortColumn.Size);

    // 生命周期

    /// <summary>启动提示，如「找不到路径…」，无提示时为 null</summary>
    public string? StartupMessage { get; private set; }

    /// <summary>窗口显示后调用：填充左侧导航树并打开默认视图</summary>
    public void Start(StartupTarget? startupTarget = null)
    {
        StartupMessage = startupTarget?.Message;

        // 后台读一次注册表 ShellNew 类型
        EnsureNewItemsLoaded();

        _startupNavigationPending = true;

        _ = Task.Run(() =>
        {
            NavigationTreeFactory.NavigationSnapshot snapshot = NavigationTreeFactory.Probe();

            _dispatcher.InvokeAsync(() =>
            {
                BuildNavigationTree(snapshot);

                if (!_startupNavigationPending)
                {
                    return;
                }

                _startupNavigationPending = false;

                ApplyStartupTarget(startupTarget);
            });
        });
    }

    /// <summary>兼容旧调用：以文件夹路径作为启动目标</summary>
    public void Start(string? startupFolder)
        => Start(string.IsNullOrEmpty(startupFolder) ? null : StartupTarget.ForFolder(startupFolder!));

    /// <summary>按启动目标导航：默认位置 / 文件夹 / 文件（父目录 + 选中）</summary>
    private void ApplyStartupTarget(StartupTarget? target)
    {
        if (target is null || target.Kind == StartupTargetKind.Default)
        {
            NavigateTo(ThisPcToken, recordHistory: false);
            return;
        }

        if (string.IsNullOrEmpty(target.FolderPath))
        {
            NavigateTo(ThisPcToken, recordHistory: false);
            return;
        }

        NavigateTo(target.FolderPath, recordHistory: false);

        // NavigateTo 会重置 _selectAfterLoad，故在其后设置文件选中目标，等 FinishLoad 消费
        if (target.Kind == StartupTargetKind.File && target.SelectItemPath is { Length: > 0 } file)
        {
            _selectAfterLoad = file;
        }
    }

    public void BuildNavigationTree()
    {
        _selectedTreeNode = null;

        NavigationTreeFactory.BuildRoots(NavigationRoots, includeRecycleBin: true);
    }

    private void BuildNavigationTree(NavigationTreeFactory.NavigationSnapshot snapshot)
    {
        _selectedTreeNode = null;

        NavigationTreeFactory.BuildRoots(NavigationRoots, snapshot, includeRecycleBin: true);
    }

    public void RefreshNavigationEntries() => BuildNavigationTree();

    /// <summary>驱动器热插拔 WM_DEVICECHANGE 后的刷新</summary>
    public void OnDrivesChanged(string changedLetters)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.InvokeAsync(() => OnDrivesChanged(changedLetters));
            return;
        }

        LogManager.Info(typeof(MainViewModel), $"驱动器变更后刷新（{changedLetters}）。");

        BuildNavigationTree();
        SyncNavigationSelection();

        // 处于「此电脑」视图 → 重新加载驱动器列表
        if (IsThisPcView)
        {
            BeginLoad();
            return;
        }

        // 当前目录所在盘被移除 → 重新加载
        if (!IsRecycleBinView && !FolderPickerViewModel.IsExistingDirectory(_currentLocation))
        {
            BeginLoad();
        }
    }


    private SubfolderLoadResult LoadChildFolders(string longPath, CancellationToken cancellationToken)
        => NavigationTreeFactory.LoadChildFolders(longPath, cancellationToken);


    // 导航命令

    public bool CanGoBack => _backStack.Count > 0;

    public bool CanGoForward => _forwardStack.Count > 0;

    public bool CanGoUp => !IsThisPcView && !IsRecycleBinView;


    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack()
    {
        if (_backStack.Count == 0)
        {
            return;
        }

        string target = _backStack[^1];
        _backStack.RemoveAt(_backStack.Count - 1);
        _forwardStack.Add(_currentLocation);
        NavigateTo(target, recordHistory: false);
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward()
    {
        if (_forwardStack.Count == 0)
        {
            return;
        }

        string target = _forwardStack[^1];
        _forwardStack.RemoveAt(_forwardStack.Count - 1);
        _backStack.Add(_currentLocation);
        NavigateTo(target, recordHistory: false);
    }

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private void GoUp()
    {
        if (IsThisPcView || IsRecycleBinView)
        {
            return;
        }


        string? parent = null;
        try
        {
            parent = PathHelper.GetParent(PathHelper.ToLong(_currentLocation));
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(MainViewModel), $"取上一级失败：{ex.Message}");
        }

        if (parent is null)
        {
            NavigateTo(ThisPcToken, recordHistory: true);
        }
        else
        {
            NavigateTo(PathHelper.ToDisplay(parent), recordHistory: true);
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        BuildNavigationTree();

        if (IsRecycleBinView)
        {
            LoadRecycleBin(force: true);
            return;
        }

        BeginLoad();
    }

    [RelayCommand]
    private void GoThisPc()
    {
        NavigateTo(ThisPcToken, recordHistory: true);
    }

    public void ActivateThisPcFolder(ThisPcFolderViewModel? folder)
    {
        if (folder is null || !folder.IsAvailable || string.IsNullOrEmpty(folder.DisplayPath))
        {
            return;
        }

        NavigateTo(folder.DisplayPath, recordHistory: true);
    }

    public void ActivateThisPcDrive(ThisPcDriveViewModel? drive)
    {
        if (drive is null || string.IsNullOrEmpty(drive.RootPath))
        {
            return;
        }

        NavigateTo(drive.RootPath, recordHistory: true);
    }

    public void NavigateToNode(NavigationNodeViewModel? node)
    {
        // 程序化设置选中项经双向绑定触发 TreeView.SelectedItemChanged，不是用户点击，必须忽略
        if (IsTreeSelectionSuppressed)
        {
            return;
        }

        if (node?.Kind == NavigationNodeKind.RecycleBin)
        {
            NavigateTo(RecycleBinToken, recordHistory: true);
            return;
        }

        if (node is null || !node.CanNavigate || string.IsNullOrEmpty(node.DisplayPath))
        {
            return;
        }


        // 已是当前位置：不重复加载
        if (string.Equals(node.DisplayPath, _currentLocation, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        NavigateTo(node.DisplayPath!, recordHistory: true);
    }

    [RelayCommand]
    private void OpenItem(FileItemViewModel? item)
    {
        Activate(item);
    }

    [RelayCommand]
    private void SortByColumn(string? column)
    {
        if (!Enum.TryParse(column, ignoreCase: true, out FileSortColumn parsed))
        {
            return;
        }

        ref FileSortColumn targetColumn = ref _sortColumn;
        ref bool targetDescending = ref _sortDescending;
        if (IsSearchResultView)
        {
            targetColumn = ref _searchSortColumn;
            targetDescending = ref _searchSortDescending;
        }

        if (targetColumn == parsed)
        {
            targetDescending = !targetDescending;
        }
        else
        {
            targetColumn = parsed;
            targetDescending = false;
        }

        ApplySort();
        RaiseHeadersChanged();
        RaiseSortStateChanged();
        PersistFolderView();
    }

    [RelayCommand]
    private void SortByMenuColumn(string? column)
    {
        if (!Enum.TryParse(column, ignoreCase: true, out FileSortColumn parsed))
        {
            return;
        }

        ref FileSortColumn targetColumn = ref _sortColumn;
        ref bool targetDescending = ref _sortDescending;
        if (IsSearchResultView)
        {
            targetColumn = ref _searchSortColumn;
            targetDescending = ref _searchSortDescending;
        }

        if (targetColumn != parsed)
        {
            targetColumn = parsed;
            targetDescending = false;
        }

        ApplySort();
        RaiseHeadersChanged();
        RaiseSortStateChanged();
        PersistFolderView();
    }


    public void Activate(FileItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        // 编辑中先结束编辑：双击别的项视为放弃改名
        if (_renamingItem is not null && !ReferenceEquals(_renamingItem, item))
        {
            CancelRename();
        }

        OpenFile(item);
    }

    public void NavigateToLocation(string target)
    {
        if (string.IsNullOrEmpty(target))
        {
            return;
        }

        NavigateTo(target, recordHistory: true);
    }

    // 递归搜索

    [RelayCommand(CanExecute = nameof(CanStartRecursiveSearch))]
    private void SearchInSubfolders()
    {
        string keyword = SearchText;
        if (string.IsNullOrWhiteSpace(keyword) || IsThisPcView || IsRecycleBinView)
        {
            return;
        }

        string root = _currentLocation;
        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        // 上一次搜索（若有）取消后重开
        CancelRecursiveSearchCore(clearResults: false);

        _searchRootDisplayPath = root;
        _searchKeyword = keyword.Trim();
        _searchSkippedCount = 0;
        _searchSkippedLinkCount = 0;
        _searchFirstBatchMs = -1;
        _searchStartedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();

        var cts = new CancellationTokenSource();
        _searchCts = cts;

        // 暂停 / 继续闸门 + 记录历史
        _searchPause?.Dispose();
        _searchPause = new SearchPauseToken();
        IsSearchPaused = false;
        RecordSearchHistory(_searchKeyword);
        ToggleSearchPauseCommand.NotifyCanExecuteChanged();

        // 搜索期间仍停在原目录，只把显示切到结果列表
        _selectedItems.Clear();
        _problematicCount = 0;
        _firstBatchReported = false;
        ListMessage = string.Empty;
        EmptyHint = string.Empty;
        SearchMessage = string.Empty;
        IsLoading = true;
        IsSearching = true;
        IsSearchResultView = true;

        // 搜索结果用自己独立的排序（不继承文件夹排序，也不写回）
        RestoreSearchSort();

        // 搜索结果用自己独立的分组（默认不分组）
        RestoreGroupState();
        PrepareGroupingForLoad();
        RaiseGroupStateChanged();

        // 搜索结果固定用「详细信息」视图
        _restoringFolderView = true;
        try
        {
            ApplyViewMode(FileViewMode.Details, IconSize);
        }
        finally
        {
            _restoringFolderView = false;
        }

        _items.BeginUpdate();
        _items.Clear();
        _items.EndUpdate();

        UpdateAddressBar();
        UpdateStatusText();
        UpdateNavigationStates();
        UpdateCommandStates();
        OnPropertyChanged(nameof(ShowSearchFolderColumn));
        SearchInSubfoldersCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanCancelSearch));

        bool skipHiddenSystem = false;
        var service = new RecursiveSearchService(batchSize: SearchBatchSize, skipHiddenAndSystem: skipHiddenSystem);
        _ = Task.Run(
            () => RunSearchOnBackground(service, root, _searchKeyword, cts.Token),
            cts.Token);
    }

    private const int SearchBatchSize = 200;

    private void RunSearchOnBackground(
        RecursiveSearchService service,
        string root,
        string keyword,
        CancellationToken cancellationToken)
    {
        try
        {
            SearchSummary summary = service.Search(
                root,
                keyword,
                hits => PushSearchHits(hits),
                progress => _dispatcher.InvokeAsync(() => OnSearchProgress(progress), DispatcherPriority.Background),
                cancellationToken,
                _searchPause);

            // 剩余缓冲先入列，再收尾（必须回到界面线程）
            _dispatcher.InvokeAsync(
                () =>
                {
                    FlushSearchPending(force: true);
                    FinishSearch(summary);
                },
                DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(MainViewModel), ex);
            _dispatcher.InvokeAsync(
                () =>
                {
                    FlushSearchPending(force: true);
                    FinishSearch(new SearchSummary(
                        _items.Count,
                        _items.Count,
                        _searchSkippedCount,
                        _searchSkippedLinkCount,
                        canceled: false,
                        error: "搜索时发生错误，详细信息已写入日志。"));
                },
                DispatcherPriority.Background);
        }
    }

    private void PushSearchHits(IReadOnlyList<SearchHit> hits)
    {
        bool schedule = false;
        int count;
        lock (_searchLock)
        {
            _searchPending ??= new List<FileEntry>(SearchBatchSize);
            foreach (SearchHit hit in hits)
            {
                _searchPending.Add(new FileEntry(
                    hit.Name,
                    hit.LongPath,
                    hit.Attributes,
                    hit.Size,
                    hit.LastWriteTimeUtc));
            }

            count = _searchPending.Count;
            if (count >= SearchBatchSize && !_searchFlushScheduled)
            {
                _searchFlushScheduled = true;
                schedule = true;
            }
        }

        if (schedule)
        {
            // 用 Background 优先级排队，界面线程自然在两次布局之间消化
            _dispatcher.InvokeAsync(() => FlushSearchPending(force: false), DispatcherPriority.Background);
        }
        else if (_searchFirstBatchMs < 0 && count > 0)
        {
            _searchFirstBatchMs = ElapsedMilliseconds();
        }
    }

    private void FlushSearchPending(bool force)
    {
        List<FileEntry>? batch;
        lock (_searchLock)
        {
            _searchFlushScheduled = false;
            if (_searchPending is null || _searchPending.Count == 0)
            {
                return;
            }

            if (!force && _searchPending.Count < SearchBatchSize)
            {
                return;
            }

            batch = _searchPending;
            _searchPending = new List<FileEntry>(SearchBatchSize);
        }

        if (batch.Count > 0 && _searchFirstBatchMs < 0)
        {
            _searchFirstBatchMs = ElapsedMilliseconds();
        }

        InsertSearchBatch(batch);
    }


    private void InsertSearchBatch(IReadOnlyList<FileEntry> batch)
    {
        var newItems = new List<FileItemViewModel>(batch.Count);
        foreach (FileEntry entry in batch)
        {
            var item = new FileItemViewModel(entry)
            {
                SearchFolderPath = PathHelper.ToDisplay(PathHelper.GetParent(entry.FullPath) ?? string.Empty),
            };

            item.IsCut = IsCutEntry(item);
            ApplyGroupKeyToNewItem(item);
            if (item.IsTooLong)
            {
                _problematicCount++;
            }

            newItems.Add(item);
        }

        if (newItems.Count == 0)
        {
            return;
        }

        _items.BeginUpdate();
        foreach (FileItemViewModel item in newItems)
        {
            _items.Add(item);
        }

        _items.EndUpdate();

        SearchMessage = string.Empty;
        UpdateStatusText();
    }

    private void OnSearchProgress(SearchProgress progress)
    {
        if (!IsSearching || !IsSearchResultView)
        {
            return;
        }
        _searchSkippedCount = progress.SkippedDirectories;
        UpdateStatusText();
    }

    private void FinishSearch(SearchSummary summary)
    {
        if (!ReferenceEquals(_searchCts, null) && _searchCts!.IsCancellationRequested)
        {
            // 取消：已有更晚的加载 / 导航接管了界面，不再回写
            return;
        }

        IsLoading = false;
        IsSearching = false;
        _searchSkippedCount = summary.SkippedDirectoryCount;
        _searchSkippedLinkCount = summary.SkippedReparsePointCount;

        DisposeSearchCts();
        DisposeSearchPause();

        if (summary.Error is { Length: > 0 } error)
        {
            ListMessage = error;
        }
        else if (summary.Canceled)
        {
            SearchMessage = "搜索已取消。";
        }
        else if (_items.Count == 0)
        {
            SearchMessage = $"未找到与“{_searchKeyword}”匹配的项目。";
        }

        ApplySort();
        UpdateEmptyHint();
        UpdateStatusText();
        UpdateNavigationStates();
        UpdateCommandStates();
        CancelSearchCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanCancelSearch));
        SearchInSubfoldersCommand.NotifyCanExecuteChanged();
    }

    public void CancelRecursiveSearch(bool clearResults)
    {
        CancelRecursiveSearchCore(clearResults);
    }

    [RelayCommand(CanExecute = nameof(CanCancelSearch))]
    private void CancelSearch()
    {
        bool wasSearchView = IsSearchResultView;
        CancelRecursiveSearchCore(clearResults: true);
        if (wasSearchView)
        {
            BeginLoad();
        }
    }

    private void CancelRecursiveSearchCore(bool clearResults)
    {
        bool hadSearch = _searchCts is not null;
        bool wasSearchView = IsSearchResultView;

        DisposeSearchCts();
        DisposeSearchPause();

        lock (_searchLock)
        {
            _searchPending = null;
            _searchFlushScheduled = false;
        }

        IsSearching = false;
        SearchMessage = string.Empty;

        if (clearResults && wasSearchView)
        {
            IsSearchResultView = false;
            OnPropertyChanged(nameof(ShowSearchFolderColumn));
        }

        if (hadSearch)
        {
            LogManager.Info(typeof(MainViewModel), $"递归搜索已取消（{_searchRootDisplayPath}，关键词“{_searchKeyword}”）。");
        }

        CancelSearchCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanCancelSearch));
        SearchInSubfoldersCommand.NotifyCanExecuteChanged();
    }

    private void DisposeSearchPause()
    {
        SearchPauseToken? pause = _searchPause;
        _searchPause = null;
        IsSearchPaused = false;
        ToggleSearchPauseCommand.NotifyCanExecuteChanged();

        try
        {
            pause?.Dispose();
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(MainViewModel), $"释放搜索暂停闸门失败：{ex.Message}");
        }
    }

    private void DisposeSearchCts()
    {
        CancellationTokenSource? cts = _searchCts;
        _searchCts = null;
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(MainViewModel), $"取消搜索失败：{ex.Message}");
        }

        cts.Dispose();
    }

    private int ElapsedMilliseconds()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long start = _searchStartedTimestamp;
        if (start == 0)
        {
            return -1;
        }

        double ms = (now - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return (int)ms;
    }


    // 命令栏

    [RelayCommand(CanExecute = nameof(CanCreateFolder))]
    private void CreateFolder()
    {
        if (!CanCreateFolder())
        {
            return;
        }

        FileOperationResult result = FileOperations.CreateDirectory(_currentLocation, FileOperations.DefaultFolderName);
        if (result.IsFailed)
        {
            ShowMessage("无法新建文件夹", result.Error);
            return;
        }

        FileEntry? entry = FileOperations.TryCreateEntry(result.LongPath);
        if (entry is null)
        {
            StatusText = "已新建文件夹，但读取其信息失败（请按 F5 刷新）。";
            LogManager.Warning(typeof(MainViewModel), $"新建文件夹后读取属性失败：{result.DisplayPath}");
            return;
        }

        var item = new FileItemViewModel(entry);
        InsertItemInSortedPosition(item);

        // 选中并立即进入原地重命名
        RequestFocus(item, beginRename: true);
        StatusText = $"已新建文件夹：{result.DisplayPath}。";

        // 压入操作历史栈
        PushCreateHistory(result.DisplayPath, isDirectory: true);
    }

    public bool CanCreateFolder() => !IsThisPcView && !IsRecycleBinView && !IsSearchResultView;

    public bool ShowNewButton => CanCreateFolder();

    public void CreateNewItem(NewMenuItem item)
    {
        if (item is null || !CanCreateFolder())
        {
            return;
        }

        FileOperationResult result = item.Kind switch
        {
            NewMenuItemKind.Folder => FileOperations.CreateDirectory(_currentLocation),
            NewMenuItemKind.Shortcut => CreateShortcut(),
            NewMenuItemKind.ShellNew when item.Entry is not null =>
                NewItemCreator.CreateFromEntry(item.Entry, _currentLocation),
            _ => FileOperationResult.Failed(Win32ErrorMessages.ErrorInvalidParameter, "不支持的新建类型。"),
        };

        FinishCreate(result, item.Kind == NewMenuItemKind.Folder ? "文件夹" : item.DisplayName);
    }

    private FileOperationResult CreateShortcut()
    {
        string? target = ShortcutTargetRequested?.Invoke();
        if (string.IsNullOrWhiteSpace(target))
        {
            return FileOperationResult.Failed(Win32ErrorMessages.ErrorInvalidParameter, string.Empty);
        }

        if (!ShortcutCreator.TryCreate(_currentLocation, target!, out string? created, out string error))
        {
            return FileOperationResult.Failed(Win32ErrorMessages.ErrorAccessDenied, error);
        }

        return FileOperationResult.Ok(created!, PathHelper.ToLong(created!));
    }

    private void FinishCreate(FileOperationResult result, string typeName)
    {
        if (result.IsFailed)
        {
            // 用户取消快捷方式目标输入：无错误文案，直接返回
            if (string.IsNullOrEmpty(result.Error))
            {
                return;
            }

            if (result.ErrorCode == Win32ErrorMessages.ErrorAccessDenied && !IsThisPcView)
            {
                ElevationAnswer answer = RequestElevation(_currentLocation, allowApplyToAll: false);
                if (answer == ElevationAnswer.Relaunch)
                {
                    RequestRelaunchElevated(_currentLocation);
                }

                return;
            }

            ShowMessage($"无法新建{typeName}", result.Error);
            StatusText = result.Error;
            return;
        }

        FileEntry? entry = FileOperations.TryCreateEntry(result.LongPath);
        if (entry is null)
        {
            StatusText = $"已新建{typeName}，但读取其信息失败（请按 F5 刷新）。";
            LogManager.Warning(typeof(MainViewModel), $"新建{typeName}后读取属性失败：{result.DisplayPath}");
            return;
        }

        var vm = new FileItemViewModel(entry);
        InsertItemInSortedPosition(vm);
        RequestFocus(vm, beginRename: true);
        StatusText = $"已新建{typeName}：{result.DisplayPath}。";

        // 压入操作历史栈
        PushCreateHistory(result.DisplayPath, isDirectory: entry.IsDirectory);
    }

    public event Func<string?>? ShortcutTargetRequested;

    public event Action<string>? RelaunchElevatedRequested;

    public void RequestRelaunchElevated(string folderDisplayPath)
        => RelaunchElevatedRequested?.Invoke(folderDisplayPath);



    [RelayCommand(CanExecute = nameof(CanRename))]
    private void Rename()
    {
        if (!CanRename())
        {
            return;
        }

        RequestFocus(_selectedItems[0], beginRename: true);
    }

    public bool CanRename() => _selectedItems.Count == 1 && _selectedItems[0].CanRename;

    [RelayCommand]
    private void OpenSelected()
    {
        if (_selectedItems.Count > 1)
        {
            OpenMultiple();
            return;
        }

        Activate(_selectedItems.Count == 1 ? _selectedItems[0] : null);
    }

    [RelayCommand(CanExecute = nameof(CanCopyPath))]
    private void CopyPath()
    {
        if (!CanCopyPath())
        {
            return;
        }

        string text = string.Join(Environment.NewLine, _selectedItems.Select(item => item.DisplayPath));

        if (!ClipboardService.TrySetText(text))
        {
            StatusText = "复制路径失败：剪贴板被其他程序占用，请稍后重试。";
            LogManager.Warning(typeof(MainViewModel), $"写剪贴板失败（{_selectedItems.Count} 个路径）。");
            return;
        }

        StatusText = _selectedItems.Count > 1
            ? $"已复制 {_selectedItems.Count} 个路径。"
            : "已复制路径。";
    }

    public bool CanCopyPath() => _selectedItems.Count > 0;

    // 重命名编辑

    public FileItemViewModel? RenamingItem => _renamingItem;

    public void BeginRename(FileItemViewModel? item)
    {
        if (item is null || !item.CanRename)
        {
            return;
        }

        CancelRename();

        _renamingItem = item;
        item.EditingName = item.Name;
        item.RenameMessage = string.Empty;
        item.IsRenameMessageError = false;
        item.IsRenaming = true;
    }

    public void EndRename()
    {
        if (_renamingItem is null)
        {
            return;
        }

        _renamingItem.IsRenaming = false;
        _renamingItem.RenameMessage = string.Empty;
        _renamingItem.IsRenameMessageError = false;
        _renamingItem = null;
    }

    public void CancelRename() => EndRename();

    public void ValidateEditingName(FileItemViewModel item, string newName)
    {
        if (item is null)
        {
            return;
        }

        NameValidationResult validation = ValidateName(item, newName);
        item.RenameMessage = validation.HasError ? validation.Error : validation.Warning;
        item.IsRenameMessageError = validation.HasError;
    }

    public bool CommitRename(FileItemViewModel item, string newName)
    {
        if (item is null)
        {
            return false;
        }

        NameValidationResult validation = ValidateName(item, newName);
        if (validation.HasError)
        {
            item.RenameMessage = validation.Error;
            item.IsRenameMessageError = true;
            return false;
        }

        // 名称没变：直接结束编辑
        if (string.Equals(item.Name, newName, StringComparison.Ordinal))
        {
            EndRename();
            StatusText = "名称未更改。";
            return true;
        }

        FileOperationResult result = FileOperations.Rename(item.LongPath, newName);
        if (result.IsFailed)
        {
            item.RenameMessage = BuildRenameFailureMessage(item, result);
            item.IsRenameMessageError = true;

            // 因权限失败 → 弹「需要管理员权限」提示，而不是普通错误框
            if (result.ErrorCode == Win32ErrorMessages.ErrorAccessDenied)
            {
                ElevationAnswer answer = RequestElevation(
                    PathHelper.GetParent(item.DisplayPath) ?? _currentLocation, allowApplyToAll: false);
                if (answer == ElevationAnswer.Relaunch)
                {
                    RequestRelaunchElevated(_currentLocation);
                }

                return false;
            }

            ShowMessage("重命名失败", item.RenameMessage);
            return false;
        }

        // 就地更新条目，再按当前排序重新定位
        string oldLongPath = item.LongPath;
        if (FileOperations.TryGetEntryInfo(
                result.LongPath,
                out uint attributes,
                out ulong size,
                out DateTime lastWriteTimeUtc))
        {
            item.ApplyRename(newName, result.LongPath, result.DisplayPath, attributes, size, lastWriteTimeUtc);
        }
        else
        {
            item.ApplyRename(newName, result.LongPath, result.DisplayPath, 0, 0, DateTime.MinValue);
        }

        // 重命名的是「当前文件夹」本身或其上级时，同步当前位置并刷新标题
        RemapCurrentLocation(PathHelper.ToDisplay(oldLongPath), item.DisplayPath);

        EndRename();
        RepositionItem(item, oldLongPath);
        StatusText = $"已重命名为：{item.Name}。";

        // 压入操作历史栈
        string originalDisplayPath = PathHelper.ToDisplay(oldLongPath);
        PushRenameHistory(originalDisplayPath, item.DisplayPath, item.LastWriteTime == DateTime.MinValue
            ? DateTime.MinValue
            : item.LastWriteTime.ToUniversalTime());
        return true;
    }

    private NameValidationResult ValidateName(FileItemViewModel item, string newName)
    {
        string parentDisplayPath = PathHelper.GetParent(item.DisplayPath) ?? string.Empty;
        return NameValidator.Validate(
            newName,
            parentDisplayPath,
            candidate => IsNameTaken(item, candidate));
    }

    private bool IsNameTaken(FileItemViewModel source, string candidate)
    {
        foreach (FileItemViewModel item in _items)
        {
            if (ReferenceEquals(item, source))
            {
                continue;
            }

            if (string.Equals(item.Name, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 列表里可能过滤掉了隐藏 / 系统项，磁盘上再确认一次
        if (string.Equals(source.Name, candidate, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? parent = PathHelper.GetParent(source.LongPath);
        return parent is not null && FileOperations.Exists(PathHelper.Combine(parent, candidate));
    }

    private static string BuildRenameFailureMessage(FileItemViewModel item, FileOperationResult result)
    {
        if (result.ErrorCode == Win32ErrorMessages.ErrorSharingViolation)
        {
            string? locking = RestartManagerService.DescribeLockingProcesses(item.DisplayPath);
            if (locking is not null)
            {
                return $"{result.Error}{locking}";
            }
        }

        return result.Error;
    }

    // 打开

    private void OpenMultiple()
    {
        List<FileItemViewModel> files = _selectedItems.Where(item => !item.IsDirectory).ToList();
        List<FileItemViewModel> folders = _selectedItems.Where(item => item.IsDirectory).ToList();

        if (folders.Count > 0)
        {
            ShowMessage("无法同时打开", "选择中既有文件夹又有文件，请只选择文件后重试。");
            return;
        }

        if (files.Count == 0)
        {
            return;
        }

        if (files.Count > MaxMultiOpenCount)
        {
            ShowMessage("打开的项目过多", $"一次最多打开 {MaxMultiOpenCount} 个文件，当前选中 {files.Count} 个。");
            return;
        }

        int opened = 0;
        int tempCopies = 0;
        int failed = 0;
        foreach (FileItemViewModel file in files)
        {
            FileOpenResult result = FileOpenService.Open(file.DisplayPath);
            if (!result.Success)
            {
                failed++;
                continue;
            }

            opened++;
            if (result.Mode == FileOpenMode.TempCopy)
            {
                tempCopies++;
            }
        }

        string summary = $"已打开 {opened} 个文件。";
        if (tempCopies > 0)
        {
            summary += $"{tempCopies} 个用临时副本打开（修改不会写回原文件）。";
        }

        if (failed > 0)
        {
            summary += $"{failed} 个打开失败。";
        }

        StatusText = summary;
    }

    private void OpenFile(FileItemViewModel item)
    {
        if (item.IsDirectory)
        {
            NavigateTo(item.DisplayPath, recordHistory: true);
            return;
        }

        if (item.IsDrive)
        {
            NavigateTo(item.DisplayPath, recordHistory: true);
            return;
        }

        FileOpenResult result = FileOpenService.Open(item.DisplayPath);
        if (!result.Success)
        {
            StatusText = result.Error;
            ShowMessage("无法打开文件", result.Error);
            return;
        }

        StatusText = result.Mode == FileOpenMode.TempCopy
            ? $"已用临时副本打开「{item.Name}」，修改不会写回原文件。"
            : $"已打开：{item.Name}。";
    }

    private void ShowMessage(string title, string message)
        => UserMessageRequested?.Invoke(this, new UserMessageEventArgs(title, message));

    // 列表增删与排序

    private void InsertItemInSortedPosition(FileItemViewModel item)
    {
        int index = 0;
        while (index < _items.Count && CompareItems(_items[index], item) <= 0)
        {
            index++;
        }

        _items.Insert(index, item);
        UpdateStatusText();
    }
    private void RepositionItem(FileItemViewModel item, string oldLongPath)
    {
        // 名称列排序时，就地更新后的名字可能已不在正确位置：先移除再按序插回
        if (_sortColumn == FileSortColumn.Name)
        {
            int current = _items.IndexOf(item);
            if (current >= 0)
            {
                _items.BeginUpdate();
                _items.RemoveAt(current);
                _items.EndUpdate();
            }

            InsertItemInSortedPosition(item);
        }

        LogManager.Info(typeof(MainViewModel), $"列表已就地更新：{oldLongPath} → {item.LongPath}");
    }

    private void RequestFocus(FileItemViewModel item, bool beginRename)
        => ItemFocusRequested?.Invoke(this, new ItemFocusEventArgs(item, beginRename));

    /// <summary>在当前筛选 / 排序结果里找出以指定前缀开头的第一项</summary>
    public FileItemViewModel? FindByPrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return null;
        }

        foreach (object entry in ItemsView)
        {
            if (entry is FileItemViewModel item
                && item.Name.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
            {
                return item;
            }
        }

        return null;
    }

    public void SelectItem(FileItemViewModel item) => RequestFocus(item, beginRename: false);
    // 命令栏

    [RelayCommand(CanExecute = nameof(CanCreateFolder))]
    private void NewMenu()
    {
        // 菜单由 DropDownButton 负责展开，这里不做事
    }

    [RelayCommand(CanExecute = nameof(CanCut))]
    private void Cut()
    {
        if (!CanCut())
        {
            return;
        }

        StatusText = _clipboard.SetFiles(ClipboardOperation.Cut, BuildClipboardEntries());
    }

    public bool CanCut() => CanCutOrCopy();

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private void Copy()
    {
        if (!CanCopy())
        {
            return;
        }

        StatusText = _clipboard.SetFiles(ClipboardOperation.Copy, BuildClipboardEntries());
    }

    public bool CanCopy() => CanCutOrCopy();

    private bool CanCutOrCopy() => _selectedItems.Count > 0 && _selectedItems.TrueForAll(item => !item.IsDrive);

    [RelayCommand(CanExecute = nameof(CanPaste))]
    private void Paste()
    {
        if (!CanPaste())
        {
            return;
        }

        if (!_clipboard.TryGetPasteData(out ClipboardOperation operation, out IReadOnlyList<ClipboardEntry> entries))
        {
            StatusText = "剪贴板里没有可粘贴的文件。";
            return;
        }

        var sources = entries.Select(entry => entry.DisplayPath).ToArray();

        // 携带历史上下文
        var history = new OperationHistoryContext(
            operation == ClipboardOperation.Cut ? OperationHistoryKind.Move : OperationHistoryKind.Copy,
            _currentLocation,
            sources);

        // 剪切粘贴成功后要清空内部剪贴板的剪切项
        FileTransferResult result = operation == ClipboardOperation.Cut
            ? _coordinator.MoveForPaste(sources, _currentLocation, history)
            : _coordinator.Copy(sources, _currentLocation, history);

        if (!result.Started)
        {
            // 预检不通过：简洁中文提示，不开始任务
            ShowMessage(operation == ClipboardOperation.Cut ? "无法移动" : "无法复制", result.Message);
            StatusText = result.Message;
            return;
        }

        StatusText = result.Message;
    }

    public bool CanPaste() => !IsThisPcView && !IsRecycleBinView && !IsSearchResultView && _clipboard.HasPasteData();


    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Delete() => DeleteSelection(permanent: false);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DeletePermanently() => DeleteSelection(permanent: true);

    public DeleteRequestResult DeleteSelection(bool permanent)
    {
        var targets = _selectedItems
            .Where(item => !item.IsDrive)
            .Select(item => item.DisplayPath)
            .ToList();

        if (targets.Count == 0)
        {
            StatusText = "没有可删除的项目。";
            return DeleteRequestResult.Rejected("没有可删除的项目。");
        }

        DeleteRequestResult result = _recycleBin.Delete(targets, permanent);

        if (result.IsCanceled)
        {
            StatusText = "已取消删除。";
            return result;
        }

        if (result.Status == DeleteRequestStatus.Rejected)
        {
            StatusText = result.Message;
            ShowMessage("无法删除", result.Message);
            return result;
        }

        StatusText = result.Message;

        // 列表即时移除对应项（永久删除完成后另有刷新）
        if (result.RecycleBinCount > 0)
        {
            RemoveItemsByPaths(targets);

            // 删除到回收站压入操作历史栈
            PushDeleteHistory();
        }

        return result;
    }

    [RelayCommand(CanExecute = nameof(CanUndoDelete))]
    private void UndoDelete()
    {
        if (_history.CanUndo)
        {
            ExecuteHistoryStep(undo: true);
        }
        else
        {
            string message = _recycleBin.UndoLastDelete();
            StatusText = message;
            if (!IsThisPcView)
            {
                Refresh();
            }
        }

        UndoDeleteCommand.NotifyCanExecuteChanged();
    }

    private void HandleAccessDeniedErrors(OperationCompletedEventArgs e)
    {
        try
        {
            List<OperationError> denied = e.Result.Errors
                .Where(err => err.ErrorCode == Core.FileSystem.Win32ErrorMessages.ErrorAccessDenied)
                .ToList();

            if (denied.Count == 0)
            {
                return;
            }

            string folder = denied
                .Select(err => string.IsNullOrEmpty(err.TargetDirectoryDisplayPath)
                    ? PathHelper.GetParent(err.DisplayPath)
                    : err.TargetDirectoryDisplayPath)
                .FirstOrDefault(path => !string.IsNullOrEmpty(path)) ?? string.Empty;

            ElevationAnswer answer = RequestElevation(folder, allowApplyToAll: denied.Count > 1);
            if (answer == ElevationAnswer.Relaunch)
            {
                RequestRelaunchElevated(string.IsNullOrEmpty(folder) ? _currentLocation : folder);
            }
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(MainViewModel), $"处理无权限失败项时出错：{ex.Message}");
        }
    }

    public bool CanUndoDelete() => _recycleBin.CanUndo;

    public IRecycleBinCoordinator RecycleBin => _recycleBin;

    private void RemoveItemsByPaths(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        var wanted = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        List<FileItemViewModel> doomed = _items.Where(item => wanted.Contains(item.DisplayPath)).ToList();
        if (doomed.Count == 0)
        {
            return;
        }

        _items.BeginUpdate();
        foreach (FileItemViewModel item in doomed)
        {
            _items.Remove(item);
            if (item.IsTooLong)
            {
                _problematicCount = Math.Max(0, _problematicCount - 1);
            }
        }

        _items.EndUpdate();

        _selectedItems.Clear();
        UpdateStatusText();
        UpdateCommandStates();
        UpdateEmptyHint();
        UndoDeleteCommand.NotifyCanExecuteChanged();
    }


    [RelayCommand(CanExecute = nameof(CanCopyOrMoveTo))]
    private void CopyTo() => RequestCopyMoveTarget(OperationKind.Copy);

    [RelayCommand(CanExecute = nameof(CanCopyOrMoveTo))]
    private void MoveTo() => RequestCopyMoveTarget(OperationKind.Move);

    public bool CanCopyOrMoveTo() => CanCutOrCopy();

    private void RequestCopyMoveTarget(OperationKind kind)
    {
        if (!CanCopyOrMoveTo())
        {
            return;
        }

        var sources = _selectedItems
            .Select(item => new CopyMoveSourceItem(item.DisplayPath, item.IsDirectory))
            .ToList();

        IReadOnlyList<CopyMoveTargetItem> targets = CopyMoveTargets.Build(_recentTargets.Items);
        var viewModel = new CopyMoveTargetViewModel(kind, sources, targets);

        CopyMoveTargetRequested?.Invoke(this, new CopyMoveTargetRequestEventArgs(kind, viewModel));
    }

    /// <summary>对话框确认后执行复制 / 移动并记入最近使用</summary>
    public FileTransferResult ExecuteCopyMoveTo(OperationKind kind, string targetDisplayPath)
    {
        if (_selectedItems.Count == 0)
        {
            return FileTransferResult.Rejected("没有要处理的项目。");
        }

        var sources = _selectedItems
            .Where(item => !item.IsDrive)
            .Select(item => item.DisplayPath)
            .ToArray();

        if (sources.Length == 0)
        {
            return FileTransferResult.Rejected("没有要处理的项目。");
        }

        FileTransferResult result = kind == OperationKind.Move
            ? _coordinator.Move(sources, targetDisplayPath, new OperationHistoryContext(
                OperationHistoryKind.Move, targetDisplayPath, sources))
            : _coordinator.Copy(sources, targetDisplayPath, new OperationHistoryContext(
                OperationHistoryKind.Copy, targetDisplayPath, sources));

        if (result.Started)
        {
            // 任务成功受理后才记入最近使用
            _recentTargets.Add(targetDisplayPath);
        }

        StatusText = result.Message;
        return result;
    }

    public bool HasSelection() => _selectedItems.Count > 0;

    // 属性 / 打开方式

    public event EventHandler<FilePropertiesViewModel>? PropertiesRequested;

    public bool CanShowProperties() => _selectedItems.Count > 0 || (!IsThisPcView && !IsRecycleBinView);


    [RelayCommand(CanExecute = nameof(CanShowProperties))]
    private void ShowProperties()
    {
        string? target = _selectedItems.Count > 0
            ? _selectedItems[0].DisplayPath
            : IsThisPcView || IsRecycleBinView ? null : _currentLocation;


        if (string.IsNullOrEmpty(target))
        {
            return;
        }

        ShowPropertiesFor(target!);
    }

    public void ShowPropertiesFor(string displayPath)
    {
        FilePropertiesViewModel? viewModel = FilePropertiesViewModel.Create(displayPath);
        if (viewModel is null)
        {
            ShowMessage("无法显示属性", $"无法读取「{displayPath}」的属性（可能已被删除或没有访问权限）。");
            return;
        }

        PropertiesRequested?.Invoke(this, viewModel);
    }

    public IReadOnlyList<OpenWithHandler> EnumerateOpenWithHandlers()
    {
        if (_selectedItems.Count != 1)
        {
            return Array.Empty<OpenWithHandler>();
        }

        FileItemViewModel item = _selectedItems[0];
        if (item.IsDirectory || item.IsDrive)
        {
            return Array.Empty<OpenWithHandler>();
        }

        return OpenWithService.EnumerateRecommended(item.DisplayPath);
    }

    public void OpenWithProgram(string executable)
    {
        if (_selectedItems.Count != 1 || string.IsNullOrEmpty(executable))
        {
            return;
        }

        FileItemViewModel item = _selectedItems[0];
        StatusText = OpenWithService.TryOpenWith(item.DisplayPath, executable, out string error)
            ? $"已用「{System.IO.Path.GetFileNameWithoutExtension(executable)}」打开：{item.Name}"
            : error;

        if (!string.IsNullOrEmpty(error))
        {
            ShowMessage("无法打开", error);
        }
    }

    public void OpenWithDialog()
    {
        if (_selectedItems.Count != 1)
        {
            return;
        }

        FileItemViewModel item = _selectedItems[0];
        if (!OpenWithService.TryOpenAsDialog(item.DisplayPath, out string error))
        {
            StatusText = error;
            ShowMessage("无法打开「打开方式」", error);
        }
    }

    public bool CanOpenWith()
        => _selectedItems.Count == 1 && !_selectedItems[0].IsDirectory && !_selectedItems[0].IsDrive;

    public bool CanOpenSelection()
    {
        if (_selectedItems.Count == 0)
        {
            return false;
        }

        if (_selectedItems.Count == 1)
        {
            return true;
        }

        if (_selectedItems.Any(item => item.IsDirectory))
        {
            return false;
        }

        return _selectedItems.Count <= MaxMultiOpenCount;
    }

    // 回收站页面

    public string RecycleBinNameColumnHeader => "名称";

    public string RecycleBinOriginalLocationColumnHeader => "原位置";

    public string RecycleBinDeletedColumnHeader => "删除日期";

    public string RecycleBinSizeColumnHeader => "大小";

    public string RecycleBinTypeColumnHeader => "类型";

    public RecycleBinSortColumn RecycleBinSortColumn => _recycleBinSortColumn;

    public bool IsRecycleBinSortDescending => _recycleBinSortDescending;

    public IReadOnlyList<RecycleBinItemViewModel> RecycleBinSelectedItems => _recycleBinSelectedItems;

    public string RecycleBinSummary
    {
        get
        {
            if (_recycleBinItems.Count == 0)
            {
                return "共 0 个项目";
            }

            long total = _recycleBinItems.Sum(item => item.SizeBytes);
            return $"共 {_recycleBinItems.Count} 个项目，合计 {DeletePlanner.FormatSize(total)}";
        }
    }

    public void UpdateRecycleBinSelection(IEnumerable<RecycleBinItemViewModel> selected)
    {
        _recycleBinSelectedItems.Clear();
        _recycleBinSelectedItems.AddRange(selected);
        UpdateRecycleBinStatusText();
        UpdateRecycleBinCommandStates();
    }

    public void NavigateToRecycleBin(bool recordHistory) => NavigateTo(RecycleBinToken, recordHistory);

    /// <summary>后台读取回收站条目，回到界面线程后填充列表</summary>
    public void LoadRecycleBin(bool force)
    {
        if (!force && _recycleBinLoaded && _recycleBinItems.Count > 0)
        {
            return;
        }

        int generation = ++_recycleBinLoadGeneration;
        IsRecycleBinLoading = true;
        RecycleBinMessage = string.Empty;
        _recycleBinSelectedItems.Clear();
        UpdateRecycleBinStatusText();
        UpdateRecycleBinCommandStates();

        _ = Task.Run(() =>
        {
            IReadOnlyList<Core.RecycleBin.RecycleBinEntry> entries;
            string error = string.Empty;
            try
            {
                entries = _recycleBin.ListEntries();
            }
            catch (Exception ex)
            {
                entries = Array.Empty<Core.RecycleBin.RecycleBinEntry>();
                error = $"读取回收站失败：{ex.Message}";
                LogManager.Error(typeof(MainViewModel), ex);
            }

            _dispatcher.InvokeAsync(
                () => FinishRecycleBinLoad(generation, entries, error),
                DispatcherPriority.Background);
        });
    }

    private void FinishRecycleBinLoad(
        int generation,
        IReadOnlyList<Core.RecycleBin.RecycleBinEntry> entries,
        string error)
    {
        if (generation != _recycleBinLoadGeneration || !IsRecycleBinView)
        {
            return;
        }

        IsRecycleBinLoading = false;
        _recycleBinLoaded = true;

        var items = entries.Select(entry => new RecycleBinItemViewModel(entry)).ToList();
        SortRecycleBinItems(items);

        _recycleBinItems.Clear();
        foreach (RecycleBinItemViewModel item in items)
        {
            _recycleBinItems.Add(item);
        }

        if (error.Length > 0)
        {
            RecycleBinMessage = error;
        }
        else if (_recycleBinItems.Count == 0)
        {
            RecycleBinMessage = "回收站为空。";
        }

        UpdateRecycleBinStatusText();
        UpdateRecycleBinCommandStates();
        RaiseRecycleBinHeadersChanged();
    }

    private void SortRecycleBinItems(List<RecycleBinItemViewModel> items)
    {
        int Compare(RecycleBinItemViewModel a, RecycleBinItemViewModel b)
        {
            int result = _recycleBinSortColumn switch
            {
                RecycleBinSortColumn.OriginalLocation => string.Compare(
                    a.OriginalPath, b.OriginalPath, StringComparison.CurrentCultureIgnoreCase),
                RecycleBinSortColumn.DeletedDate => CompareDateTime(a.DeletedLocal, b.DeletedLocal),
                RecycleBinSortColumn.Size => a.SizeBytes.CompareTo(b.SizeBytes),
                RecycleBinSortColumn.Type => string.Compare(
                    a.TypeName, b.TypeName, StringComparison.CurrentCultureIgnoreCase),
                _ => NaturalComparer.Compare(a.SortName, b.SortName),
            };

            if (result == 0 && _recycleBinSortColumn != RecycleBinSortColumn.Name)
            {
                result = NaturalComparer.Compare(a.SortName, b.SortName);
            }

            return _recycleBinSortDescending ? -result : result;
        }

        items.Sort(Compare);
    }

    [RelayCommand]
    private void SortRecycleBinByColumn(string? column)
    {
        if (!Enum.TryParse(column, ignoreCase: true, out RecycleBinSortColumn parsed))
        {
            return;
        }

        if (_recycleBinSortColumn == parsed)
        {
            _recycleBinSortDescending = !_recycleBinSortDescending;
        }
        else
        {
            _recycleBinSortColumn = parsed;
            // 删除日期 / 大小默认倒序，其余升序
            _recycleBinSortDescending = parsed is RecycleBinSortColumn.DeletedDate or RecycleBinSortColumn.Size;
        }

        var items = _recycleBinItems.ToList();

        // 记住选中项，排序后恢复
        var selectedIds = new HashSet<string>(
            _recycleBinSelectedItems.Select(item => item.Entry.Id),
            StringComparer.OrdinalIgnoreCase);

        SortRecycleBinItems(items);

        _recycleBinItems.Clear();
        foreach (RecycleBinItemViewModel item in items)
        {
            _recycleBinItems.Add(item);
        }

        RecycleBinSelectionRestoreRequested?.Invoke(
            this,
            items.Where(item => selectedIds.Contains(item.Entry.Id)).ToList());

        // 回收站排序持久化
        _folderViews?.SetRecycleBinSort(_recycleBinSortColumn.ToString(), _recycleBinSortDescending);

        RaiseRecycleBinHeadersChanged();
    }

    public event EventHandler<IReadOnlyList<RecycleBinItemViewModel>>? RecycleBinSelectionRestoreRequested;

    [RelayCommand(CanExecute = nameof(HasRecycleBinSelection))]
    private void RestoreRecycleBin()
        => RunRecycleBinOperation(_recycleBin.Restore);

    [RelayCommand(CanExecute = nameof(HasRecycleBinSelection))]
    private void DeleteRecycleBinPermanently()
        => RunRecycleBinOperation(_recycleBin.DeletePermanently);

    [RelayCommand(CanExecute = nameof(CanEmptyRecycleBin))]
    private void EmptyRecycleBin()
    {
        if (!CanEmptyRecycleBin())
        {
            return;
        }

        RunRecycleBinOperation(_ => _recycleBin.Empty());
    }

    public bool HasRecycleBinSelection() => _recycleBinSelectedItems.Count > 0;

    public bool CanEmptyRecycleBin() => _recycleBinItems.Count > 0;

    // 必须后台跑：还原可能同步等待冲突对话框答复，在界面线程会与模态框互相阻塞
    private void RunRecycleBinOperation(
        Func<IReadOnlyList<Core.RecycleBin.RecycleBinEntry>, RecycleBinOperationResult> operation)
    {
        var selected = _recycleBinSelectedItems.Select(item => item.Entry).ToList();

        RecycleBinOperationResult result;
        try
        {
            result = operation(selected);
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(MainViewModel), ex);
            result = RecycleBinOperationResult.Failed($"操作失败：{ex.Message}");
        }

        if (result.IsCanceled)
        {
            UpdateRecycleBinStatusText();
            return;
        }

        if (result.Changed)
        {
            LoadRecycleBin(force: true);
        }
        else if (!string.IsNullOrEmpty(result.Message))
        {
            StatusText = result.Message;
        }
    }

    private void UpdateRecycleBinStatusText()
    {
        StatusText = RecycleBinSummary;
    }

    private void UpdateRecycleBinCommandStates()
    {
        RestoreRecycleBinCommand.NotifyCanExecuteChanged();
        DeleteRecycleBinPermanentlyCommand.NotifyCanExecuteChanged();
        EmptyRecycleBinCommand.NotifyCanExecuteChanged();
    }

    private void RaiseRecycleBinHeadersChanged()
    {
        OnPropertyChanged(nameof(RecycleBinSortColumn));
        OnPropertyChanged(nameof(IsRecycleBinSortDescending));
        OnPropertyChanged(nameof(RecycleBinSummary));
    }

    // 视图模式


    public const double MinIconSize = 16;

    public const double MaxIconSize = 256;

    public const double DefaultIconSize = 96;

    public event EventHandler? ViewModeChanged;

    [ObservableProperty]
    private FileViewMode _viewMode = FileViewMode.Details;

    [ObservableProperty]
    private double _iconSize = DefaultIconSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsRowHeight))]
    [NotifyPropertyChangedFor(nameof(DetailsRowStyleKey))]
    private bool _isCompactMode;

    public const double DetailsRowHeightNormal = 28;

    public const double DetailsRowHeightCompact = 22;

    public double DetailsRowHeight => IsCompactMode ? DetailsRowHeightCompact : DetailsRowHeightNormal;

    public string DetailsRowStyleKey => IsCompactMode ? "FileListRowCompactStyle" : "FileListRowStyle";

    private bool _suppressIconSizeSync;

    [RelayCommand]
    private void SetViewMode(string? mode)
    {
        if (Enum.TryParse(mode, ignoreCase: true, out FileViewMode parsed))
        {
            ApplyViewMode(parsed, FileViewModeInfo.GetIconSize(parsed));
        }
    }

    public void ApplyViewMode(FileViewMode mode) => ApplyViewMode(mode, FileViewModeInfo.GetIconSize(mode));

    public void ApplyViewMode(FileViewMode mode, double iconSize)
    {
        _suppressIconSizeSync = true;
        try
        {
            ViewMode = mode;
            if (FileViewModeInfo.IsIconView(mode))
            {
                IconSize = Math.Clamp(iconSize, MinIconSize, MaxIconSize);
            }
        }
        finally
        {
            _suppressIconSizeSync = false;
        }

        RaiseViewModeChanged();
    }

    public void SetIconSize(double size)
    {
        IconSize = Math.Clamp(size, MinIconSize, MaxIconSize);
    }

    public bool IsViewMode(FileViewMode mode) => ViewMode == mode;

    public bool IsDetailsView => ViewMode == FileViewMode.Details;

    public bool IsNonDetailsView => ViewMode != FileViewMode.Details;

    public bool ShowFileListView
        => (IsDetailsView || IsSearchResultView) && !IsRecycleBinView && !IsThisPcView;

    public bool ShowIconListView
        => IsNonDetailsView && !IsSearchResultView && !IsRecycleBinView && !IsThisPcView;

    public bool ShowThisPcPage => IsThisPcView;

    public bool ShowSearchResultsView => IsSearchResultView;

    public bool IsCommandBarApplicable => !IsRecycleBinView;

    /// <summary>工具栏「修复路径…」按钮是否可见：与命令可用页面一致（此电脑 / 回收站之外）</summary>
    public bool ShowRepairPathsButton => CanRepairPaths();


    public bool IsIconView => FileViewModeInfo.IsIconView(ViewMode);

    public bool IsSmallIconsView => ViewMode == FileViewMode.SmallIcons;

    public bool IsListView => ViewMode == FileViewMode.List;

    public bool IsTilesView => ViewMode == FileViewMode.Tiles;

    public bool IsContentView => ViewMode == FileViewMode.Content;

    public string ViewModeName => FileViewModeInfo.GetName(ViewMode);

    /// <summary>图标图片的实际边长，夹在 16~256</summary>
    public double TileImageSize => Math.Clamp(IconSize, MinIconSize, MaxIconSize);

    public double TileNameFontSize => TileImageSize >= 128 ? 14 : TileImageSize >= 64 ? 13 : 12;

    public const double TileNameLineHeight = 16;

    public const int TileNameMaxLines = 2;

    public double TileNameAreaHeight => TileNameLineHeight * TileNameMaxLines;

    public Size IconTileSize => new(
        Math.Max(TileImageSize + 12, 76),
        TileImageSize + TileNameAreaHeight + TileVerticalPadding);

    private const double TileVerticalPadding = 12;

    public Size ItemSize => ViewMode switch
    {
        FileViewMode.Tiles => new Size(230, 54),
        FileViewMode.SmallIcons => new Size(190, 22),
        FileViewMode.List => new Size(230, 22),
        FileViewMode.Content => new Size(320, 60),
        _ => IconTileSize,
    };

    public double ItemContentWidth => Math.Max(40, ItemSize.Width - 6);

    public string ZoomText => IsIconView ? $"{TileImageSize:0} px" : ViewModeName;

    private void RaiseViewModeChanged()
    {
        OnPropertyChanged(nameof(IsDetailsView));
        OnPropertyChanged(nameof(IsNonDetailsView));
        OnPropertyChanged(nameof(ShowFileListView));
        OnPropertyChanged(nameof(ShowIconListView));
        OnPropertyChanged(nameof(ShowThisPcPage));

        OnPropertyChanged(nameof(IsIconView));
        OnPropertyChanged(nameof(IsSmallIconsView));
        OnPropertyChanged(nameof(IsListView));
        OnPropertyChanged(nameof(IsTilesView));
        OnPropertyChanged(nameof(IsContentView));
        OnPropertyChanged(nameof(ViewModeName));
        OnPropertyChanged(nameof(TileImageSize));
        OnPropertyChanged(nameof(TileNameFontSize));
        OnPropertyChanged(nameof(IconTileSize));
        OnPropertyChanged(nameof(ItemSize));
        OnPropertyChanged(nameof(ItemContentWidth));
        OnPropertyChanged(nameof(ZoomText));
        OnPropertyChanged(nameof(DetailsRowHeight));
        OnPropertyChanged(nameof(DetailsRowStyleKey));
        UpdateViewCommandStates();
        ViewModeChanged?.Invoke(this, EventArgs.Empty);

        // 视图 / 缩放变化后写入「每文件夹视图记忆」
        PersistFolderView();
    }

    partial void OnViewModeChanged(FileViewMode value) => RaiseViewModeChanged();

    partial void OnIconSizeChanged(double value)
    {
        if (!_suppressIconSizeSync && !IsDetailsView)
        {
            FileViewMode target = FileViewModeInfo.FromIconSize(value);
            if (ViewMode != target)
            {
                ViewMode = target;
                return;
            }
        }

        RaiseViewModeChanged();
    }

    [RelayCommand(CanExecute = nameof(CanZoom))]
    private void ZoomIn() => SetIconSize(TileImageSize >= 200 ? MaxIconSize : TileImageSize * 1.25);

    [RelayCommand(CanExecute = nameof(CanZoom))]
    private void ZoomOut() => SetIconSize(TileImageSize <= 20 ? MinIconSize : TileImageSize / 1.25);

    public bool CanZoom() => IsNonDetailsView;

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);

    public event EventHandler? SettingsRequested;


    [RelayCommand]
    private void TogglePreviewPane()
    {
        IsPreviewPaneOpen = !IsPreviewPaneOpen;
    }

    [RelayCommand]
    private void ToggleCompactMode() => IsCompactMode = !IsCompactMode;

    // 选中

    public void UpdateSelection(IEnumerable<FileItemViewModel> selected)
    {
        _selectedItems.Clear();
        _selectedItems.AddRange(selected);
        UpdateStatusText();
        UpdateCommandStates();

        // 预览窗格跟随选中项
        PreviewSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? PreviewSelectionChanged;

    public IReadOnlyList<FileItemViewModel> SelectedItems => _selectedItems;

    public Services.Preview.PreviewTarget? BuildPreviewTarget()
    {
        if (_selectedItems.Count != 1)
        {
            return null;
        }

        FileItemViewModel item = _selectedItems[0];

        // 驱动器条目：图标 48 + 名称 + 类型 + 容量，无路径
        return new Services.Preview.PreviewTarget
        {
            Name = item.Name,
            DisplayPath = item.DisplayPath,
            LongPath = item.LongPath,
            IsDirectory = item.IsDirectory,
            IsDrive = item.IsDrive,
            Size = item.SizeBytes,
            LastWriteTime = item.LastWriteTime,
            TypeName = item.TypeName,
            Icon48 = item.GetIconForSize(48),
            SizeText = item.SizeText,
            IssueReason = item.IssueReason,
            HasBadge = item.HasBadge,
        };
    }

    public void OpenPreviewTarget(string displayPath)
    {
        FileItemViewModel? item = _items.FirstOrDefault(
            candidate => string.Equals(candidate.DisplayPath, displayPath, StringComparison.OrdinalIgnoreCase));

        if (item is not null)
        {
            Activate(item);
        }
    }

    public void CopyToDesktopItems(IReadOnlyList<string> displayPaths)
    {
        if (displayPaths.Count == 0)
        {
            return;
        }

        CopyToDesktopResult result = _coordinator.CopyToDesktop(displayPaths);
        StatusText = result.Message;
        if (!result.Success)
        {
            ShowMessage("无法复制到桌面", result.Message);
        }
    }

    // 搜索结果命令

    public bool CanOpenContainingFolder()
        => IsSearchResultView && _selectedItems.Count == 1;

    [RelayCommand(CanExecute = nameof(CanOpenContainingFolder))]
    private void OpenContainingFolder()
    {
        if (_selectedItems.Count != 1)
        {
            return;
        }

        FileItemViewModel item = _selectedItems[0];
        string folder = item.IsDirectory
            ? PathHelper.ToDisplay(PathHelper.GetParent(item.LongPath) ?? string.Empty)
            : item.SearchFolderPath;

        if (string.IsNullOrEmpty(folder))
        {
            folder = PathHelper.ToDisplay(PathHelper.GetParent(item.LongPath) ?? string.Empty);
        }

        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        // 导航离开搜索结果视图，并在加载完成后选中原条目
        _selectionToRestore = new HashSet<string>(new[] { item.DisplayPath }, StringComparer.OrdinalIgnoreCase);
        NavigateTo(folder, recordHistory: true);
    }


    private void UpdateCommandStates()
    {
        RenameCommand.NotifyCanExecuteChanged();
        CopyPathCommand.NotifyCanExecuteChanged();
        CutCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        DeletePermanentlyCommand.NotifyCanExecuteChanged();
        UndoDeleteCommand.NotifyCanExecuteChanged();
        CopyToCommand.NotifyCanExecuteChanged();
        MoveToCommand.NotifyCanExecuteChanged();
        ShowPropertiesCommand.NotifyCanExecuteChanged();
        OpenContainingFolderCommand.NotifyCanExecuteChanged();
        UpdateViewCommandStates();
    }

    private void UpdateViewCommandStates()
    {
        ZoomInCommand.NotifyCanExecuteChanged();
        ZoomOutCommand.NotifyCanExecuteChanged();
    }

    public void RefreshPasteState() => PasteCommand.NotifyCanExecuteChanged();

    public void SetStatusMessage(string message)
    {
        if (!string.IsNullOrEmpty(message))
        {
            StatusText = message;
        }
    }


    // 剪贴板

    private ClipboardEntry[] BuildClipboardEntries()
        => _selectedItems
            .Select(item => new ClipboardEntry(item.DisplayPath, item.LongPath, item.IsDirectory))
            .ToArray();

    private void OnClipboardChanged(object? sender, EventArgs e)
    {
        // 剪贴板可能由后台 / 其它线程改动，统一回到界面线程处理
        if (_dispatcher.CheckAccess())
        {
            ApplyClipboardState();
        }
        else
        {
            _dispatcher.InvokeAsync(ApplyClipboardState);
        }
    }

    private void OnOperationCompleted(object? sender, OperationCompletedEventArgs e)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.InvokeAsync(() => OnOperationCompleted(sender, e));
            return;
        }

        if (e.Result.Success || e.Result.Errors.Count > 0)
        {
            // 取消且无失败时就不必覆盖其它提示
            StatusText = e.Result.Message;
        }

        // 批量操作中有项因权限失败时统一提示
        HandleAccessDeniedErrors(e);

        // 复制 / 移动成功后压入操作历史栈
        if (e.Result.Success)
        {
            PushTransferHistory(e.History, e.Result);
        }

        // 永久删除任务在后台进行时用户可能已切到回收站页面 → 完成后刷新该页面
        if (IsRecycleBinView)
        {
            LoadRecycleBin(force: true);
            return;
        }

        if (IsThisPcView)
        {
            return;
        }


        // 永久删除没有目标目录，只按源目录判断刷新
        bool targetIsCurrent = false;
        if (!string.IsNullOrEmpty(e.Result.TargetDirectoryDisplayPath))
        {
            try
            {
                targetIsCurrent = string.Equals(
                    PathHelper.ToDisplay(PathHelper.ToLong(_currentLocation)),
                    PathHelper.ToDisplay(PathHelper.ToLong(e.Result.TargetDirectoryDisplayPath)),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                LogManager.Debug(typeof(MainViewModel), $"判断目标目录失败：{ex.Message}");
            }
        }

        bool sourceIsCurrent = e.SourceDirectories.Any(dir =>
            string.Equals(dir, _currentLocation, StringComparison.OrdinalIgnoreCase));

        if (!targetIsCurrent && !sourceIsCurrent)
        {
            return;
        }

        // 记住当前选中项路径，刷新后尽量恢复
        _selectionToRestore = new HashSet<string>(
            _selectedItems.Select(item => item.DisplayPath),
            StringComparer.OrdinalIgnoreCase);

        Refresh();
    }

    private void ApplyClipboardState()
    {
        foreach (FileItemViewModel item in _items)
        {
            item.IsCut = IsCutEntry(item);
        }

        RefreshPasteState();
    }

    private bool IsCutEntry(FileItemViewModel item)
        => _clipboard.IsCut
           && _clipboard.Entries.Any(entry =>
               string.Equals(entry.DisplayPath, item.DisplayPath, StringComparison.OrdinalIgnoreCase));

    // 名称筛选

    partial void OnIsThisPcViewChanged(bool value)
    {
        // 切换「此电脑」视图时刷新命令与按钮可用性
        CreateFolderCommand.NotifyCanExecuteChanged();
        NewMenuCommand.NotifyCanExecuteChanged();
        PasteCommand.NotifyCanExecuteChanged();
        ShowPropertiesCommand.NotifyCanExecuteChanged();
        SearchInSubfoldersCommand.NotifyCanExecuteChanged();

        OnPropertyChanged(nameof(ShowThisPcPage));
        OnPropertyChanged(nameof(ShowFileListView));
        OnPropertyChanged(nameof(ShowIconListView));

        // 位置名变化
        RaiseTitleChanged();
    }

    partial void OnIsRecycleBinViewChanged(bool value)
    {
        // 回收站页面不适用命令，只保留页面命令
        OnPropertyChanged(nameof(IsCommandBarApplicable));
        OnPropertyChanged(nameof(ShowFileListView));
        OnPropertyChanged(nameof(ShowIconListView));
        OnPropertyChanged(nameof(CanGoUp));
        CreateFolderCommand.NotifyCanExecuteChanged();
        NewMenuCommand.NotifyCanExecuteChanged();
        PasteCommand.NotifyCanExecuteChanged();
        ShowPropertiesCommand.NotifyCanExecuteChanged();
        SearchInSubfoldersCommand.NotifyCanExecuteChanged();
        RefreshPasteState();

        // 位置名变化
        RaiseTitleChanged();
    }

    partial void OnIsSearchResultViewChanged(bool value)
        // 搜索结果视图的位置名是「搜索“关键词”」
        => RaiseTitleChanged();


    partial void OnSearchTextChanged(string value)
    {
        // 清空搜索框即取消递归搜索并退出结果视图
        if (string.IsNullOrEmpty(value) && IsSearchResultView)
        {
            CancelRecursiveSearch(clearResults: true);
            BeginLoad();
        }

        SearchInSubfoldersCommand.NotifyCanExecuteChanged();
        ItemsView.Refresh();
        UpdateEmptyHint();
    }

    private bool FilterItem(object item)
    {
        if (item is not FileItemViewModel file)
        {
            return false;
        }

        // 递归搜索结果已是按关键词匹配出来的，不再做即时筛选
        if (IsSearchResultView)
        {
            return true;
        }

        string keyword = SearchText;
        return string.IsNullOrEmpty(keyword)
            || file.Name.Contains(keyword, StringComparison.CurrentCultureIgnoreCase);
    }

    // 加载

    private void NavigateTo(string location, bool recordHistory)
    {
        // 手动导航作废尚未执行的启动导航
        _startupNavigationPending = false;

        if (IsSearchResultView || _searchCts is not null)
        {
            CancelRecursiveSearch(clearResults: true);
        }

        if (recordHistory && !string.IsNullOrEmpty(_currentLocation))
        {
            _backStack.Add(_currentLocation);
            _forwardStack.Clear();
        }

        // 导航到刚才所在文件夹的父目录时选中那个子文件夹（资源管理器行为）
        _selectAfterLoad = IsParentOf(location, _currentLocation) ? _currentLocation : null;

        _currentLocation = location;
        BeginLoad();
    }

    private static bool IsParentOf(string parent, string child)
    {
        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(child)
            || string.Equals(parent, ThisPcToken, StringComparison.Ordinal)
            || string.Equals(child, ThisPcToken, StringComparison.Ordinal)
            || string.Equals(parent, RecycleBinToken, StringComparison.Ordinal)
            || string.Equals(child, RecycleBinToken, StringComparison.Ordinal))
        {
            return false;
        }


        try
        {
            string? childParent = PathHelper.GetParent(PathHelper.ToLong(child));
            return childParent is not null
                   && string.Equals(
                       PathHelper.ToDisplay(PathHelper.Normalize(childParent)),
                       PathHelper.ToDisplay(PathHelper.Normalize(parent)),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(MainViewModel), $"判断父子关系失败：{ex.Message}");
            return false;
        }
    }

    private void BeginLoad()
    {
        // 普通目录加载前先退出搜索结果视图
        if (IsSearchResultView)
        {
            CancelRecursiveSearch(clearResults: true);
        }

        // 取消上一次加载（代次 + 取消标记双保险）
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        _loadGeneration++;
        int generation = _loadGeneration;

        _showHidden = ExplorerSettings.ShowHiddenFiles;
        _showSystem = ExplorerSettings.ShowSystemFiles;

        // 切换目录时放弃正在进行的重命名编辑
        CancelRename();

        _selectedItems.Clear();
        _problematicCount = 0;
        _firstBatchReported = false;
        ListMessage = string.Empty;
        EmptyHint = string.Empty;
        IsAccessDenied = false;
        IsLocationUnavailable = false;
        LocationUnavailableMessage = string.Empty;
        IsLoading = true;
        IsThisPcView = string.Equals(_currentLocation, ThisPcToken, StringComparison.Ordinal);
        IsRecycleBinView = string.Equals(_currentLocation, RecycleBinToken, StringComparison.Ordinal);

        if (IsRecycleBinView)
        {
            // 回收站页面不枚举文件系统，条目在后台按需读取
            _items.BeginUpdate();
            _items.Clear();
            _items.EndUpdate();

            RestoreRecycleBinViewState();
            UpdateAddressBar();
            UpdateStatusText();
            UpdateNavigationStates();
            UpdateCommandStates();
            CancelRename();
            SyncNavigationSelection();
            IsLoading = false;

            // 每次进入都重新读取，让刚删除的项目立刻出现
            LoadRecycleBin(force: true);
            return;
        }


        // 进入目录时按「每文件夹视图记忆」恢复视图与排序
        RestoreFolderView();

        // 此电脑 / 回收站不适用分组
        if (IsThisPcView || IsRecycleBinView)
        {
            _groupBy = FileGroupBy.None;
            _groupDescending = false;
            ApplyGrouping();
            RaiseGroupStateChanged();
        }

        _items.BeginUpdate();
        _items.Clear();
        _items.EndUpdate();

        UpdateAddressBar();
        UpdateStatusText();
        UpdateNavigationStates();
        UpdateCommandStates();
        CreateFolderCommand.NotifyCanExecuteChanged();
        SyncNavigationSelection();

        if (IsThisPcView)
        {
            _ = Task.Run(() => LoadDrivesOnBackground(generation, cts.Token), cts.Token);
            return;
        }

        string longPath;
        try
        {
            longPath = PathHelper.ToLong(_currentLocation);
        }
        catch (Exception ex)
        {
            FinishWithError(generation, ex.Message);
            return;
        }

        _ = Task.Run(() => EnumerateOnBackground(generation, longPath, cts.Token), cts.Token);
    }

    private void RestoreRecycleBinViewState()
    {
        // 视图固定为「详细信息」
        _restoringFolderView = true;
        try
        {
            ApplyViewMode(FileViewMode.Details, DefaultIconSize);

            // 回收站排序持久化（独立于文件夹记忆）
            if (_folderViews is not null)
            {
                _recycleBinSortColumn = Enum.TryParse(
                    _folderViews.RecycleBinSortColumn, ignoreCase: true, out RecycleBinSortColumn column)
                    ? column
                    : RecycleBinSortColumn.DeletedDate;
                _recycleBinSortDescending = _folderViews.RecycleBinSortDescending;
            }

            RaiseHeadersChanged();
            RaiseSortStateChanged();
        }
        finally
        {
            _restoringFolderView = false;
        }
    }

    private void RestoreFolderView()
    {
        if (IsThisPcView)
        {
            return;
        }

        _restoringFolderView = true;
        try
        {
            Core.Settings.FolderViewState? state = _folderViews?.TryGet(_currentLocation);
            if (state is null)
            {
                // 没有记录：回到默认，避免沿用上一个文件夹的视图
                ApplyViewMode(FileViewMode.Details, DefaultIconSize);
                _sortColumn = FileSortColumn.Name;
                _sortDescending = false;
            }
            else
            {
                if (Enum.TryParse(state.ViewMode, ignoreCase: true, out FileViewMode mode)
                    && Enum.IsDefined(mode))
                {
                    ApplyViewMode(mode, state.IconSize);
                }

                if (Enum.TryParse(state.SortColumn, ignoreCase: true, out FileSortColumn column))
                {
                    _sortColumn = column;
                    _sortDescending = state.SortDescending;
                }
            }

            // 恢复「分组依据」并重建组描述
            RestoreGroupState();
            PrepareGroupingForLoad();
            RaiseGroupStateChanged();

            RaiseHeadersChanged();
            RaiseSortStateChanged();
        }
        finally
        {
            _restoringFolderView = false;
        }
    }

    private void PersistFolderView()
    {
        if (_restoringFolderView || _folderViews is null)
        {
            return;
        }

        if (IsSearchResultView)
        {
            _folderViews.SetSearchSort(_searchSortColumn.ToString(), _searchSortDescending);
            _folderViews.SetSearchGroup(_searchGroupBy.ToString(), _searchGroupDescending);
            return;
        }

        if (IsThisPcView || IsRecycleBinView)
        {
            return;
        }

        _folderViews.Set(
            _currentLocation,
            ViewMode,
            IconSize,
            _sortColumn.ToString(),
            _sortDescending,
            _groupBy.ToString(),
            _groupDescending);
    }

    private void RestoreSearchSort()
    {
        if (_folderViews is null)
        {
            _searchSortColumn = FileSortColumn.Name;
            _searchSortDescending = false;
            return;
        }

        _searchSortColumn = Enum.TryParse(_folderViews.SearchSortColumn, ignoreCase: true, out FileSortColumn column)
            ? column
            : FileSortColumn.Name;
        _searchSortDescending = _folderViews.SearchSortDescending;
    }


    private void EnumerateOnBackground(int generation, string longPath, CancellationToken cancellationToken)
    {
        var enumerator = new DirectoryEnumerator(StreamingBatchSize);
        var pending = new List<FileEntry>(FlushThreshold);
        int flushedItems = 0;

        DirectoryEnumerationResult result = enumerator.Enumerate(
            longPath,
            batch =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                pending.AddRange(batch);
                flushedItems += batch.Count;

                int threshold = flushedItems <= FirstFlushThreshold ? FirstFlushThreshold : FlushThreshold;
                if (pending.Count >= threshold)
                {
                    FlushPending(generation, pending);
                    pending = new List<FileEntry>(FlushThreshold);
                }
            },
            cancellationToken);

        if (pending.Count > 0)
        {
            FlushPending(generation, pending);
        }

        _dispatcher.InvokeAsync(
            () => FinishLoad(generation, result),
            DispatcherPriority.Background);
    }

    private void FlushPending(int generation, List<FileEntry> batch)
    {
        _dispatcher.InvokeAsync(
            () => AppendBatch(generation, batch),
            DispatcherPriority.Background);
    }

    private void LoadDrivesOnBackground(int generation, CancellationToken cancellationToken)
    {
        IReadOnlyList<DriveItem> drives = LocationProvider.GetDrives();

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        _dispatcher.InvokeAsync(
            () => FinishDrives(generation, drives),
            DispatcherPriority.Background);
    }

    private void AppendBatch(int generation, IReadOnlyList<FileEntry> batch)
    {
        if (generation != _loadGeneration)
        {
            return;
        }

        var newItems = new List<FileItemViewModel>(batch.Count);
        foreach (FileEntry entry in batch)
        {
            if (entry.IsSystem && !_showSystem)
            {
                continue;
            }

            if (entry.IsHidden && !_showHidden)
            {
                continue;
            }

        var item = new FileItemViewModel(entry);
        item.IsCut = IsCutEntry(item);
        ApplyGroupKeyToNewItem(item);
        if (item.IsTooLong)
            {
                _problematicCount++;
            }

            newItems.Add(item);
        }

        if (newItems.Count == 0)
        {
            return;
        }

        _items.BeginUpdate();
        foreach (FileItemViewModel item in newItems)
        {
            _items.Add(item);
        }

        _items.EndUpdate();

        // 只在首批刷新状态栏，避免每批重新布局
        if (!IsLoading || !_firstBatchReported)
        {
            _firstBatchReported = true;
            UpdateStatusText();
        }
    }

    private bool _firstBatchReported;

    private void FinishDrives(int generation, IReadOnlyList<DriveItem> drives)
    {
        if (generation != _loadGeneration)
        {
            return;
        }

        _items.BeginUpdate();
        foreach (DriveItem drive in drives)
        {
            _items.Add(new FileItemViewModel(drive));
        }

        _items.EndUpdate();

        PopulateThisPcPage(drives);

        IsLoading = false;
        if (_items.Count == 0)
        {
            ListMessage = "未检测到任何驱动器。";
        }

        UpdateEmptyHint();
        UpdateStatusText();
        UpdateNavigationStates();

        if (_pendingTreeReveal)
        {
            _pendingTreeReveal = false;
            _ = ExpandAndSelectTreeNodeAsync();
        }
    }

    private void PopulateThisPcPage(IReadOnlyList<DriveItem> drives)
    {
        ThisPcFolders.Clear();
        foreach (KnownFolderItem folder in LocationProvider.GetKnownFolders())
        {
            if (folder.IsAvailable)
            {
                ThisPcFolders.Add(new ThisPcFolderViewModel(folder));
            }
        }

        ThisPcDrives.Clear();
        foreach (DriveItem drive in drives)
        {
            ThisPcDrives.Add(new ThisPcDriveViewModel(drive));
        }

        OnPropertyChanged(nameof(ThisPcFolderGroupHeader));
        OnPropertyChanged(nameof(ThisPcDriveGroupHeader));
    }

    private void FinishLoad(int generation, DirectoryEnumerationResult result)
    {
        if (generation != _loadGeneration)
        {
            return;
        }

        IsLoading = false;

        if (result.Canceled)
        {
            return;
        }

        ApplySort();

        // 没有「每文件夹视图记忆」时按内容定默认视图
        ApplyDefaultViewModeIfNeeded();

        if (!result.Success)
        {
            if (result.ErrorCode == Core.FileSystem.Win32ErrorMessages.ErrorAccessDenied)
            {
                IsAccessDenied = true;
                IsLocationUnavailable = false;
                LocationUnavailableMessage = string.Empty;
                ListMessage = "你没有访问此文件夹的权限。";
            }
            else if (IsUnavailableLocationError(result.ErrorCode))
            {
                // 位置不可用 or 当前文件夹被外部删除 —— 在后台判定，避免网络路径挂起阻塞 UI
                IsAccessDenied = false;
                IsLocationUnavailable = false;
                LocationUnavailableMessage = string.Empty;
                ListMessage = string.Empty;
                ResolveUnavailableLocationAsync(generation, _currentLocation);
            }
            else
            {
                IsAccessDenied = false;
                IsLocationUnavailable = false;
                LocationUnavailableMessage = string.Empty;
                ListMessage = result.Error;
            }
        }

        UpdateEmptyHint();

        // 返回上一级后选中并滚动到刚才所在的子文件夹
        if (_selectAfterLoad is { } target)
        {
            _selectAfterLoad = null;
            FileItemViewModel? match = _items.FirstOrDefault(
                item => string.Equals(item.DisplayPath, target, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                RequestFocus(match, beginRename: false);
            }
        }

        RestoreSelectionAfterRefresh();

        UpdateStatusText();
        UpdateNavigationStates();

        // 列表加载完成后再展开 / 滚动左侧树（避免与列表枚举争抢磁盘）
        if (_pendingTreeReveal)
        {
            _pendingTreeReveal = false;
            _ = ExpandAndSelectTreeNodeAsync();
        }
    }

    private static bool IsUnavailableLocationError(int errorCode)
        => errorCode is Core.FileSystem.Win32ErrorMessages.ErrorInvalidDrive
            or Core.FileSystem.Win32ErrorMessages.ErrorNotReady
            or Core.FileSystem.Win32ErrorMessages.ErrorPathNotFound
            or Core.FileSystem.Win32ErrorMessages.ErrorFileNotFound;

    private void ResolveUnavailableLocationAsync(int generation, string location)
    {
        _ = Task.Run(() =>
        {
            string? nearest = LocationAvailability.FindNearestExistingAncestor(
                location,
                LocationAvailability.DefaultTimeoutMilliseconds,
                out bool timedOut);

            _dispatcher.InvokeAsync(
                () => ResolveUnavailableLocationOnUi(generation, location, nearest, timedOut),
                DispatcherPriority.Background);
        });
    }

    private void ResolveUnavailableLocationOnUi(int generation, string location, string? nearest, bool timedOut)
    {
        // 已被更晚的加载 / 导航接管 → 丢弃
        if (generation != _loadGeneration
            || !string.Equals(location, _currentLocation, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!timedOut && nearest is { Length: > 0 }
            && !string.Equals(nearest, location, StringComparison.OrdinalIgnoreCase))
        {
            // 当前文件夹已不存在 → 回到最近存在的上级并提示
            _pendingStatusHint = $"原位置已不存在，已返回「{nearest}」。";
            NavigateTo(nearest, recordHistory: true);
            return;
        }

        // 整段路径不可用 → 提示 + 返回上一级按钮
        IsLocationUnavailable = true;
        LocationUnavailableMessage = "此位置不可用";
        ListMessage = string.Empty;
        OnPropertyChanged(nameof(ShowReturnUpButton));
        UpdateEmptyHint();
        UpdateStatusText();
    }

    private string? _pendingStatusHint;

    public void UpdateEmptyHint()
    {
        // 搜索结果视图用自己的搜索提示，不用空文件夹提示
        if (IsSearchResultView)
        {
            EmptyHint = string.Empty;
            return;
        }

        if (IsThisPcView || IsRecycleBinView || IsLoading || !string.IsNullOrEmpty(ListMessage)
            || IsLocationUnavailable || IsAccessDenied)
        {
            EmptyHint = string.Empty;
            return;
        }

        int visible = ItemsView.IsEmpty ? 0 : 1;
        if (visible > 0)
        {
            EmptyHint = string.Empty;
            return;
        }

        EmptyHint = !string.IsNullOrEmpty(SearchText) && _items.Count > 0
            ? "没有与搜索条件匹配的项。"
            : "此文件夹为空。";
    }

    private void ApplyDefaultViewModeIfNeeded()
    {
        if (IsThisPcView || _items.Count == 0 || _folderViews is null)
        {
            return;
        }

        // 已有记录 → 不覆盖用户的选择
        if (_folderViews.TryGet(_currentLocation) is not null)
        {
            return;
        }


        int imageCount = 0;
        int fileCount = 0;
        foreach (FileItemViewModel item in _items)
        {
            if (item.IsDirectory)
            {
                continue;
            }

            fileCount++;
            if (item.IsImage)
            {
                imageCount++;
            }
        }

        FileViewMode mode = FolderViewStore.DefaultViewModeFor(imageCount, fileCount);
        if (ViewMode != mode)
        {
            ApplyViewMode(mode);
        }
    }

    private void RestoreSelectionAfterRefresh()
    {
        HashSet<string>? wanted = _selectionToRestore;
        _selectionToRestore = null;

        if (wanted is null || wanted.Count == 0)
        {
            return;
        }

        List<FileItemViewModel> matches = _items
            .Where(item => wanted.Contains(item.DisplayPath))
            .ToList();

        if (matches.Count == 0)
        {
            return;
        }

        SelectionRestoreRequested?.Invoke(this, matches);
    }

    public event EventHandler<IReadOnlyList<FileItemViewModel>>? SelectionRestoreRequested;

    private void FinishWithError(int generation, string error)
    {
        if (generation != _loadGeneration)
        {
            return;
        }

        IsLoading = false;
        ListMessage = error;
        UpdateEmptyHint();
        _pendingTreeReveal = false;
        UpdateStatusText();
        UpdateNavigationStates();
    }

    // 排序

    private void ApplySort()
    {
        if (_items.Count == 0)
        {
            return;
        }

        List<FileItemViewModel> sorted = _items.ToList();
        sorted.Sort(CompareItems);

        // 只有顺序真的变了才重建，避免无谓 Reset 造成滚动跳动
        bool sameOrder = true;
        for (int i = 0; i < sorted.Count; i++)
        {
            if (!ReferenceEquals(sorted[i], _items[i]))
            {
                sameOrder = false;
                break;
            }
        }

        if (sameOrder)
        {
            return;
        }

        _items.BeginUpdate();
        _items.Clear();
        foreach (FileItemViewModel item in sorted)
        {
            _items.Add(item);
        }

        _items.EndUpdate();
    }

    private int CompareItems(FileItemViewModel a, FileItemViewModel b)
    {
        // 按「所在文件夹」列排序时不做文件夹前置分组
        bool byFolder = IsSearchResultView && _searchSortColumn == FileSortColumn.Folder;
        if (!byFolder && a.IsDirectory != b.IsDirectory)
        {
            return a.IsDirectory ? -1 : 1;
        }

        FileSortColumn column = IsSearchResultView ? _searchSortColumn : _sortColumn;
        bool descending = IsSearchResultView ? _searchSortDescending : _sortDescending;

        int result = column switch
        {
            FileSortColumn.DateModified => CompareDateTime(a.LastWriteTime, b.LastWriteTime),
            FileSortColumn.Type => string.Compare(a.TypeName, b.TypeName, StringComparison.CurrentCultureIgnoreCase),
            FileSortColumn.Size => a.SizeBytes.CompareTo(b.SizeBytes),
            FileSortColumn.Folder => string.Compare(
                a.SearchFolderPath, b.SearchFolderPath, StringComparison.CurrentCultureIgnoreCase),
            _ => NaturalComparer.Compare(a.SortName, b.SortName),
        };

        if (result == 0 && column != FileSortColumn.Name)
        {
            result = NaturalComparer.Compare(a.SortName, b.SortName);
        }

        return descending ? -result : result;
    }


    private static int CompareDateTime(DateTime a, DateTime b)
    {
        // 无时间（MinValue）排在有时间的之后
        if (a == DateTime.MinValue && b == DateTime.MinValue)
        {
            return 0;
        }

        if (a == DateTime.MinValue)
        {
            return 1;
        }

        if (b == DateTime.MinValue)
        {
            return -1;
        }

        return a.CompareTo(b);
    }

    private string BuildColumnHeader(string title, FileSortColumn column)
    {
        // 排序箭头由界面单独绘制，这里只返回标题
        _ = column;
        return title;
    }

    private void RaiseHeadersChanged()
    {
        OnPropertyChanged(nameof(NameColumnHeader));
        OnPropertyChanged(nameof(DateColumnHeader));
        OnPropertyChanged(nameof(TypeColumnHeader));
        OnPropertyChanged(nameof(SizeColumnHeader));
        OnPropertyChanged(nameof(SearchFolderColumnHeader));
        OnPropertyChanged(nameof(SortColumn));
    }

    // 排序菜单状态

    /// <summary>当前排序列；搜索结果视图用独立的搜索排序列</summary>
    public FileSortColumn SortColumn => IsSearchResultView ? _searchSortColumn : _sortColumn;

    public bool IsSortByName => SortColumn == FileSortColumn.Name;

    public bool IsSortByDateModified => SortColumn == FileSortColumn.DateModified;

    public bool IsSortByType => SortColumn == FileSortColumn.Type;

    public bool IsSortBySize => SortColumn == FileSortColumn.Size;

    public bool IsSortByFolder => SortColumn == FileSortColumn.Folder;

    public bool IsSortAscending => !(IsSearchResultView ? _searchSortDescending : _sortDescending);


    public bool IsSortDescending => IsSearchResultView ? _searchSortDescending : _sortDescending;

    [RelayCommand]
    private void SetSortAscending()
    {
        if (IsSearchResultView)
        {
            if (!_searchSortDescending)
            {
                return;
            }

            _searchSortDescending = false;
        }
        else
        {
            if (!_sortDescending)
            {
                return;
            }

            _sortDescending = false;
        }

        ApplySort();
        RaiseHeadersChanged();
        RaiseSortStateChanged();
        PersistFolderView();
    }

    [RelayCommand]
    private void SetSortDescending()
    {
        if (IsSearchResultView)
        {
            if (_searchSortDescending)
            {
                return;
            }

            _searchSortDescending = true;
        }
        else
        {
            if (_sortDescending)
            {
                return;
            }

            _sortDescending = true;
        }

        ApplySort();
        RaiseHeadersChanged();
        RaiseSortStateChanged();
        PersistFolderView();
    }

    private void RaiseSortStateChanged()
    {
        OnPropertyChanged(nameof(IsSortByName));
        OnPropertyChanged(nameof(IsSortByDateModified));
        OnPropertyChanged(nameof(IsSortByType));
        OnPropertyChanged(nameof(IsSortBySize));
        OnPropertyChanged(nameof(IsSortByFolder));
        OnPropertyChanged(nameof(IsSortAscending));
        OnPropertyChanged(nameof(IsSortDescending));
        OnPropertyChanged(nameof(SortColumn));
    }


    // 状态 / 地址栏刷新

    private void UpdateAddressBar()
    {
        CurrentLocationDisplay = _currentLocation;
        OnPropertyChanged(nameof(SearchLocationText));

        // 位置变化后刷新标题
        RaiseTitleChanged();
    }

    private void SyncNavigationSelection()
    {
        // 回收站页面：选中导航树里的「回收站」节点（它没有路径）
        if (IsRecycleBinView)
        {
            SetSelectedTreeNode(NavigationRoots.FirstOrDefault(node => node.Kind == NavigationNodeKind.RecycleBin));
            _pendingTreeReveal = false;
            return;
        }

        SetSelectedTreeNode(FindNode(NavigationRoots, _currentLocation));
        _pendingTreeReveal = true;
    }


    /// <summary>程序化设置树选中项时置位，抑制 TreeView.SelectedItemChanged 反向触发导航</summary>
    public bool IsTreeSelectionSuppressed { get; private set; }

    private bool _pendingTreeReveal;

    public event EventHandler<NavigationNodeViewModel>? NavigationRevealRequested;

    private async Task ExpandAndSelectTreeNodeAsync()
    {
        string location = _currentLocation;
        int generation = _loadGeneration;

        try
        {
            if (!_dispatcher.CheckAccess())
            {
                await _dispatcher.InvokeAsync(() => ExpandAndSelectTreeNodeAsync()).Task.ConfigureAwait(true);
                return;
            }

            NavigationNodeViewModel? target = await FindOrExpandTreeNodeAsync(location).ConfigureAwait(true);

            // 展开过程中用户又导航了：放弃结果
            if (generation != _loadGeneration || !string.Equals(location, _currentLocation, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (target is null)
            {
                return;
            }

            target.IsExpanded = true;
            SetSelectedTreeNode(target);
            NavigationRevealRequested?.Invoke(this, target);
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(MainViewModel), $"展开导航树失败（{location}）：{ex.Message}");
        }
    }
    private async Task<NavigationNodeViewModel?> FindOrExpandTreeNodeAsync(string location)
    {
        if (string.Equals(location, ThisPcToken, StringComparison.Ordinal))
        {
            return FindNode(NavigationRoots, location);
        }

        NavigationNodeViewModel? known = FindKnownFolderNode(location);
        if (known is not null)
        {
            return known;
        }

        NavigationNodeViewModel? root = TryParseIntoRootAndSegments(location, out string rootPath, out string[] segments);
        if (root is null)
        {
            return null;
        }

        NavigationNodeViewModel current = root;
        foreach (string segment in segments)
        {
            await current.EnsureChildrenLoadedAsync().ConfigureAwait(true);

            NavigationNodeViewModel? child = current.Children.FirstOrDefault(
                node => !node.IsPlaceholder
                        && string.Equals(node.DisplayName, segment, StringComparison.OrdinalIgnoreCase));
            if (child is null)
            {
                // 中间某层没找到对应节点（例如被隐藏）：停在已找到的最深一层
                break;
            }

            current.IsExpanded = true;
            current = child;
        }

        return current;
    }

    private NavigationNodeViewModel? FindKnownFolderNode(string location)
    {
        foreach (NavigationNodeViewModel root in NavigationRoots)
        {
            foreach (NavigationNodeViewModel child in root.Children)
            {
                if (child.Kind == NavigationNodeKind.KnownFolder
                    && PathEquals(child.DisplayPath, location))
                {
                    return child;
                }
            }
        }

        return null;
    }

    private NavigationNodeViewModel? TryParseIntoRootAndSegments(
        string location,
        out string rootPath,
        out string[] segments)
    {
        rootPath = string.Empty;
        segments = Array.Empty<string>();

        if (PathHelper.IsUnc(location))
        {
            return null;
        }

        string parsedRoot;
        string[] parsedSegments;
        try
        {
            string display = PathHelper.ToDisplay(PathHelper.Normalize(location));

            if (display.Length < 2 || display[1] != ':')
            {
                return null;
            }

            parsedRoot = display.Substring(0, 2) + "\\";
            string rest = display.Length > 3 ? display.Substring(3) : string.Empty;
            parsedSegments = rest.Length == 0
                ? Array.Empty<string>()
                : rest.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(MainViewModel), $"拆分导航路径失败（{location}）：{ex.Message}");
            return null;
        }

        rootPath = parsedRoot;
        segments = parsedSegments;

        NavigationNodeViewModel? thisPc = NavigationRoots.FirstOrDefault(
            node => node.Kind == NavigationNodeKind.ThisPc);
        if (thisPc is null)
        {
            return null;
        }

        thisPc.IsExpanded = true;

        return thisPc.Children.FirstOrDefault(node => PathEquals(node.DisplayPath, parsedRoot));
    }

    private static bool PathEquals(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return false;
        }

        return string.Equals(
            FolderViewStore.NormalizeKey(a),
            FolderViewStore.NormalizeKey(b),
            StringComparison.OrdinalIgnoreCase);
    }

    private void SetSelectedTreeNode(NavigationNodeViewModel? match)
    {
        // 程序化改选中项会经双向绑定触发 TreeView.SelectedItemChanged，置位抑制标记避免误当成用户点击
        IsTreeSelectionSuppressed = true;
        try
        {
            if (ReferenceEquals(match, _selectedTreeNode))
            {
                return;
            }

            if (_selectedTreeNode is not null)
            {
                _selectedTreeNode.IsSelected = false;
            }

            _selectedTreeNode = match;
            if (match is not null)
            {
                match.IsSelected = true;
            }
        }
        finally
        {
            // 事件可能在设置过程中同步触发，用 Dispatcher 延后复位覆盖整条同步链
            _dispatcher.InvokeAsync(
                () => IsTreeSelectionSuppressed = false,
                System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private static NavigationNodeViewModel? FindNode(IEnumerable<NavigationNodeViewModel> nodes, string location)
    {
        foreach (NavigationNodeViewModel node in nodes)
        {
            if (!node.IsPlaceholder
                && node.CanNavigate
                && string.Equals(node.DisplayPath, location, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            if (node.Children.Count > 0)
            {
                NavigationNodeViewModel? child = FindNode(node.Children, location);
                if (child is not null)
                {
                    return child;
                }
            }
        }

        return null;
    }

    private void UpdateNavigationStates()
    {
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
        GoUpCommand.NotifyCanExecuteChanged();
    }

    private void UpdateStatusText()
    {
        // 回收站页面：状态栏显示回收站自己的汇总
        if (IsRecycleBinView)
        {
            StatusText = RecycleBinSummary;
            return;
        }

        var builder = new StringBuilder();

        // 递归搜索结果视图的状态栏
        if (IsSearchResultView)
        {
            if (IsSearching)
            {
                builder.Append(IsSearchPaused
                    ? $"已暂停 · 已找到 {_items.Count} 个"
                    : $"正在搜索… 已找到 {_items.Count} 个项目");
            }
            else
            {
                builder.Append($"共 {_items.Count} 个项目");
                if (_searchSkippedCount > 0)
                {
                    builder.Append($"   |   跳过 {_searchSkippedCount} 个无法访问的文件夹");
                }

                if (_searchSkippedLinkCount > 0)
                {
                    builder.Append($"   |   跳过 {_searchSkippedLinkCount} 个链接");
                }
            }

            if (_selectedItems.Count > 0)
            {
                builder.Append($"   |   已选择 {_selectedItems.Count} 个");
            }

            StatusText = builder.ToString();
            return;
        }

        if (IsLoading)
        {
            builder.Append("正在加载…  ");
        }

        // 「此电脑」页面状态栏统计「文件夹 + 设备和驱动器」
        int itemCount = IsThisPcView ? ThisPcFolders.Count + ThisPcDrives.Count : _items.Count;
        builder.Append($"共 {itemCount} 个项目");

        if (_selectedItems.Count > 0)
        {
            builder.Append($"   |   已选择 {_selectedItems.Count} 个");

            ulong totalSize = 0;
            bool hasFile = false;
            foreach (FileItemViewModel item in _selectedItems)
            {
                if (!item.IsDirectory)
                {
                    totalSize += item.SizeBytes;
                    hasFile = true;
                }
            }

            if (hasFile)
            {
                builder.Append($"（合计 {FileItemViewModel.FormatSize(totalSize)}）");
            }
        }

        if (_problematicCount > 0)
        {
            builder.Append($"   |   {_problematicCount} 个路径超长");
        }

        // 一次性提示追加在末尾；加载中先不消费
        if (!IsLoading && _pendingStatusHint is { Length: > 0 } hint)
        {
            _pendingStatusHint = null;
            builder.Append($"   |   {hint}");
        }

        StatusText = builder.ToString();
    }

    [RelayCommand]
    private void ReturnUp()
    {
        string? parent = null;
        try
        {
            parent = PathHelper.GetParent(PathHelper.ToLong(_currentLocation));
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(MainViewModel), $"取上一级失败：{ex.Message}");
        }

        if (parent is null)
        {
            NavigateTo(ThisPcToken, recordHistory: true);
            return;
        }

        // 上级本身也可能不可用：在后台找最近可达的上级，避免连点；用代次守卫作废过期的异步跳转
        string parentDisplay = PathHelper.ToDisplay(parent);
        int generation = _loadGeneration;
        _ = Task.Run(() =>
        {
            string? reachable = LocationAvailability.FindNearestExistingAncestor(
                parentDisplay,
                LocationAvailability.DefaultTimeoutMilliseconds,
                out bool timedOut);

            _dispatcher.InvokeAsync(
                () =>
                {
                    if (generation != _loadGeneration)
                    {
                        return;
                    }

                    NavigateTo(
                        !timedOut && reachable is { Length: > 0 } ? reachable : ThisPcToken,
                        recordHistory: true);
                },
                DispatcherPriority.Background);
        });
    }
}


