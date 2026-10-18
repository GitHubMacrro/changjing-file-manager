using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ChangJing.App.Core.FileSystem;
using ChangJing.App.Core.LongPath;
using ChangJing.App.Core.Operations;
using ChangJing.App.Core.RecycleBin;
using ChangJing.App.Core.Settings;
using ChangJing.App.Services;
using ChangJing.App.ViewModels;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Views;

/// <summary>主窗口：文件列表、导航、排序与快捷键都在这里接线</summary>
public partial class MainWindow : Wpf.Ui.Controls.FluentWindow, IDeleteConfirmation
{
    private const double MinNavigationWidth = 120;

    private static readonly TimeSpan TypeAheadResetTimeout = TimeSpan.FromSeconds(1);

    private readonly AppServices _services;
    private readonly AppSettings _settings;
    private readonly MainViewModel _viewModel;

    private readonly FolderViewStore _folderViews;

    private readonly StringBuilder _typeAheadBuffer = new();
    private DateTime _typeAheadLastInput = DateTime.MinValue;

    private bool _isClosing;
    private bool _restoringColumnWidths;

    private bool _exitConfirmed;

    // 避免「临时副本已修改」询问重复弹出
    private bool _tempCopyPrompted;

    private HwndSource? _hwndSource;

    private DispatcherTimer? _deviceRefreshTimer;

    private DispatcherTimer? _toastTimer;

    private readonly StartupTarget? _startupTarget;

    public MainWindow()
        : this(new AppServices(new SettingsService(), new ThemeService()), new AppSettings())
    {
    }

    public MainWindow(AppServices services, AppSettings settings)
        : this(services, settings, recycleBinOptions: null)
    {
    }

    public MainWindow(
        AppServices services,
        AppSettings settings,
        RecycleBinOptions? recycleBinOptions,
        StartupTarget? startupTarget = null)
    {
        _services = services;
        _settings = settings;
        _startupTarget = startupTarget;
        _folderViews = new FolderViewStore(services.SettingsService, settings);

        RecycleBinOptions options = recycleBinOptions ?? new RecycleBinOptions();
        RecycleBinCapacity.Apply(settings, options);

        var recycleBinStore = new RecycleBinStore(options);

        var coordinator = new OperationCoordinator(new FileTransferService(), Dispatcher, AppClipboard.Current);

        var recycleBinCoordinator = new RecycleBinCoordinator(recycleBinStore, coordinator, this)
        {
            DeleteWithoutRecycleBin = settings.DeleteWithoutRecycleBin,
        };

        _viewModel = new MainViewModel(
            Dispatcher,
            coordinator,
            new RecentTargetStore(services.SettingsService),
            _folderViews,
            recycleBinCoordinator)
        {
            NavigationWidth = Math.Max(settings.NavigationWidth, MinNavigationWidth),
            PreviewPaneWidth = Math.Max(settings.PreviewPaneWidth, MainViewModel.MinPreviewPaneWidth),
            IsPreviewPaneOpen = settings.IsPreviewPaneOpen,
        };

        _viewModel.SetSearchHistory(new Services.Search.SearchHistoryStore(services.SettingsService, settings));

        InitializeComponent();

        DataContext = _viewModel;

        RestoreWindowPlacement();
        RegisterShortcuts();
        RegisterViewModelEvents();
        RegisterContextMenus();
        RegisterClipboardListener();
        InitializeViewModes();
        RegisterDragDrop();
        RestoreColumnWidths();

        // DropDownButton.Flyout 要 ContextMenu，所以和右键菜单同源
        SortDropDownButton.Flyout = BuildSortContextMenu();
        ViewDropDownButton.Flyout = BuildViewContextMenu();

        NewDropDownButton.Flyout = BuildNewContextMenu();

        // 列头在 ListView 模板里，用 AddHandler 统一处理点击
        FileListView.AddHandler(
            GridViewColumnHeader.ClickEvent,
            new RoutedEventHandler(OnColumnHeaderClick));

        RecycleBinListView.AddHandler(
            GridViewColumnHeader.ClickEvent,
            new RoutedEventHandler(OnColumnHeaderClick));

        // 拖拽列宽 → 记入 settings.json
        foreach (GridViewColumn column in EnumerateTrackedColumns())
        {
            DependencyPropertyDescriptor.FromProperty(GridViewColumn.WidthProperty, typeof(GridViewColumn))
                .AddValueChanged(column, OnColumnWidthChanged);
        }

        // 关掉 ListView 内置 TextSearch，类型跳转自己实现
        FileListView.SetValue(ItemsControl.IsTextSearchEnabledProperty, false);

        // Ctrl+单击已选中项取消选中，WPF 默认不干这事
        foreach (System.Windows.Controls.ListView list in new[] { FileListView, IconListView })
        {
            list.PreviewMouseLeftButtonDown += OnFileListPreviewMouseLeftButtonDownToggle;
        }

        // GridViewColumn 不在可视化树里，不能绑 DataContext，列头只能代码更新
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateColumnHeaders();
        UpdateRecycleBinColumnHeaders();
        ApplySearchFolderColumn();

        PreviewPaneControl.OpenRequested += OnPreviewOpenRequested;
        PreviewPaneControl.CopyToDesktopRequested += OnPreviewCopyToDesktopRequested;
        PreviewPaneControl.RepairRequested += OnPreviewRepairRequested;
        _viewModel.PreviewSelectionChanged += OnPreviewSelectionChanged;
        _viewModel.PropertyChanged += OnPreviewPanePropertyChanged;

        _viewModel.ApplyViewMode(ParseViewMode(settings.ViewMode), settings.IconSize);
        _viewModel.IsCompactMode = settings.IsCompactMode;
        _viewModel.ShowCheckBoxes = settings.ShowCheckBoxes;
        ApplyDetailsRowStyle();

        Loaded += OnWindowLoaded;
        Closing += OnWindowClosing;
    }

    public MainViewModel ViewModel => _viewModel;

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        // 未加载的窗口没法监视主题，所以等到这里才订阅
        _services.ThemeService.WatchWindow(this);

        InitializeTitleBar();

        _viewModel.Start(_startupTarget);

        // 找不到路径的提示：状态栏 + 非模态信息框，不阻塞
        if (_viewModel.StartupMessage is { Length: > 0 } message)
        {
            StatusBarMessage(message);
            ShowUserMessage("畅径", message);
        }
    }

    // 剪贴板监听

    private void RegisterClipboardListener()
    {
        Activated += (_, _) => _viewModel.RefreshPasteState();
    }

    // WM_CLIPBOARDUPDATE 挂这里，系统剪贴板变了就刷新粘贴菜单
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            _hwndSource = source;
            source.AddHook(WndProc);
            NativeMethods.AddClipboardFormatListener(source.Handle);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmClipboardUpdate)
        {
            _viewModel.RefreshPasteState();
        }
        else if (msg == NativeMethods.WmDeviceChange)
        {
            OnDeviceChange(wParam, lParam);
        }

        return IntPtr.Zero;
    }

    private void UnregisterClipboardListener()
    {
        if (_hwndSource is null)
        {
            return;
        }

        NativeMethods.RemoveClipboardFormatListener(_hwndSource.Handle);
        _hwndSource.RemoveHook(WndProc);
        _hwndSource = null;
    }

    // 设备变更（热插拔）

    private void OnDeviceChange(IntPtr wParam, IntPtr lParam)
    {
        int evt = wParam.ToInt32();
        if (evt != NativeMethods.DbtDeviceArrival && evt != NativeMethods.DbtDeviceRemoveComplete)
        {
            return;
        }

        if (!TryReadVolumeMask(lParam, out uint mask) || mask == 0)
        {
            return;
        }

        string letters = DescribeDriveLetters(mask);
        LogManager.Info(typeof(MainWindow), $"检测到驱动器变更（{(evt == NativeMethods.DbtDeviceArrival ? "插入" : "移除")}）：{letters}");

        ScheduleDeviceRefresh(letters);
    }

    // 从设备广播里解析盘符位掩码，非卷广播返回 false
    private static bool TryReadVolumeMask(IntPtr lParam, out uint mask)
    {
        mask = 0;
        if (lParam == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var header = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.DEV_BROADCAST_HDR>(lParam);
            if (header.dbch_devicetype != NativeMethods.DbtDevTypVolume)
            {
                return false;
            }

            var volume = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.DEV_BROADCAST_VOLUME>(lParam);
            mask = volume.dbcv_unitmask;
            return true;
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(MainWindow), $"解析设备变更广播失败：{ex.Message}");
            return false;
        }
    }

    // 位掩码转「E:」形式，仅用于日志
    private static string DescribeDriveLetters(uint mask)
    {
        var letters = new System.Text.StringBuilder();
        for (int i = 0; i < 26; i++)
        {
            if ((mask & (1u << i)) != 0)
            {
                if (letters.Length > 0)
                {
                    letters.Append(' ');
                }

                letters.Append((char)('A' + i)).Append(':');
            }
        }

        return letters.Length == 0 ? "（未知盘符）" : letters.ToString();
    }

    // 去抖：连续广播只刷新一次（约 300 ms）
    private void ScheduleDeviceRefresh(string letters)
    {
        if (_deviceRefreshTimer is null)
        {
            _deviceRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(300),
            };

            _deviceRefreshTimer.Tick += OnDeviceRefreshTick;
        }

        _deviceRefreshTimer.Stop();
        _deviceRefreshTimer.Tag = letters;
        _deviceRefreshTimer.Start();
    }

    private void OnDeviceRefreshTick(object? sender, EventArgs e)
    {
        if (_deviceRefreshTimer is null)
        {
            return;
        }

        _deviceRefreshTimer.Stop();
        string letters = _deviceRefreshTimer.Tag as string ?? string.Empty;

        try
        {
            _viewModel.OnDrivesChanged(letters);
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(MainWindow), ex);
        }
    }

    private void StopDeviceWatcher()
    {
        if (_deviceRefreshTimer is not null)
        {
            _deviceRefreshTimer.Stop();
            _deviceRefreshTimer.Tick -= OnDeviceRefreshTick;
            _deviceRefreshTimer = null;
        }
    }

    private static void LongPathOpenCleanup()
    {
        OpenCleanup.CleanupAll();
    }

    // 退出前问临时副本是否另存回原位置，防丢数据
    private async Task PromptModifiedTempCopiesAsync()
    {
        IReadOnlyList<TempOpenCopy.CopyRecord> modified = TempOpenCopy.ActiveCopies
            .Where(TempOpenCopy.IsModified)
            .ToList();

        if (modified.Count == 0)
        {
            return;
        }

        try
        {
            string detail = modified.Count == 1
                ? $"临时副本「{PathHelper.GetName(modified[0].SourceDisplayPath)}」已被修改。"
                : $"{modified.Count} 个临时副本已被修改。";

            var box = new Wpf.Ui.Controls.MessageBox
            {
                Title = "临时副本已被修改",
                Content = detail + "\n\n是否将修改另存回原位置？（否则修改将被丢弃）",
                Owner = this,
                PrimaryButtonText = "是，另存回原位置",
                CloseButtonText = "否，丢弃修改",
                IsCloseButtonEnabled = true,
            };

            // 同步阻塞会死锁，关闭与异步框都在 UI 线程
            Wpf.Ui.Controls.MessageBoxResult result =
                await box.ShowDialogAsync(showAsDialog: true, cancellationToken: CancellationToken.None);

            if (result != Wpf.Ui.Controls.MessageBoxResult.Primary)
            {
                return;
            }

            int saved = 0;
            var failures = new List<string>();
            foreach (TempOpenCopy.CopyRecord record in modified)
            {
                if (TempOpenCopy.TrySaveBack(record, out string error))
                {
                    saved++;
                }
                else
                {
                    failures.Add($"{PathHelper.GetName(record.SourceDisplayPath)}：{error}");
                }
            }

            if (failures.Count > 0)
            {
                ShowUserMessage(
                    "另存回原位置部分失败",
                    $"已另存 {saved} 个，{failures.Count} 个失败：\n" + string.Join("\n", failures));
            }
            else
            {
                StatusBarMessage($"已将 {saved} 个临时副本的修改另存回原位置。");
            }
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(MainWindow), ex);
        }
    }

    // 快捷键

    private void RegisterShortcuts()
    {
        // Alt+P 切预览窗格
        var togglePreview = new RoutedCommand();
        InputBindings.Add(new KeyBinding(togglePreview, Key.P, ModifierKeys.Alt));
        CommandBindings.Add(new CommandBinding(
            togglePreview,
            (_, _) => _viewModel.TogglePreviewPaneCommand.Execute(null)));
    }

    /// <summary>窗口级快捷键，与资源管理器一致</summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        // WPF 里按住 Alt 时 e.Key 恒为 Key.System，真键在 e.SystemKey，得归一化
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        // 这几个组合在文本框里用不到，地址栏 / 搜索框聚焦时也要生效
        if (ctrl && shift && key == Key.N)
        {
            Execute(_viewModel.CreateFolderCommand);
            e.Handled = true;
            return;
        }

        if (ctrl && shift && key == Key.C)
        {
            Execute(_viewModel.CopyPathCommand);
            e.Handled = true;
            return;
        }

        // Ctrl+Shift+1..8 切 8 种视图，回收站页面固定详细信息不响应
        if (!_viewModel.IsRecycleBinView && !_viewModel.IsSearchResultView
            && ctrl && shift && TryGetViewShortcutNumber(key, out int viewNumber))
        {
            FileViewMode? mode = FileViewModeInfo.FromShortcutNumber(viewNumber);
            if (mode is not null)
            {
                _viewModel.ApplyViewMode(mode.Value);
            }

            e.Handled = true;
            return;
        }

        // Ctrl+F / Ctrl+E / F3 聚焦搜索框，但正在重命名时不抢键（会因失焦取消编辑）
        if (_viewModel.RenamingItem is null
            && ((ctrl && (key == Key.F || key == Key.E)) || (key == Key.F3 && !alt && !ctrl)))
        {
            FocusSearchBox();
            e.Handled = true;
            return;
        }

        // 焦点在文本框时放行，让文本正常编辑
        if (IsTextBoxFocused())
        {
            return;
        }

        // Esc 取消递归搜索
        if (key == Key.Escape && (_viewModel.IsSearchResultView || _viewModel.HasActiveSearch))
        {
            Execute(_viewModel.CancelSearchCommand);
            e.Handled = true;
            return;
        }

        // Esc 在列表里取消全部选择（重命名编辑框的 Esc 已在上面的文本框分支放行）
        if (key == Key.Escape && !_viewModel.IsRecycleBinView && _viewModel.CanClearSelection())
        {
            Execute(_viewModel.ClearSelectionCommand);
            e.Handled = true;
            return;
        }

        // Ctrl+Space 切换焦点项
        if (key == Key.Space && ctrl)
        {
            ToggleFocusedItemSelection();
            e.Handled = true;
            return;
        }

        switch (key)
        {
            case Key.Left when alt:
                Execute(_viewModel.GoBackCommand);
                e.Handled = true;
                break;

            case Key.Right when alt:
                Execute(_viewModel.GoForwardCommand);
                e.Handled = true;
                break;

            case Key.Up when alt:
                Execute(_viewModel.GoUpCommand);
                e.Handled = true;
                break;

            case Key.Back:
                Execute(_viewModel.GoBackCommand);
                e.Handled = true;
                break;

            case Key.F5:
                Execute(_viewModel.RefreshCommand);
                e.Handled = true;
                break;

            case Key.R when ctrl && !shift:
                Execute(_viewModel.RefreshCommand);
                e.Handled = true;
                break;

            case Key.F2:
                Execute(_viewModel.RenameCommand);
                e.Handled = true;
                break;

            case Key.Enter when alt:
                Execute(_viewModel.ShowPropertiesCommand);
                e.Handled = true;
                break;

            case Key.A when ctrl && !shift:
                if (_viewModel.IsRecycleBinView)
                {
                    RecycleBinListView.SelectAll();
                    RecycleBinListView.Focus();
                }
                else
                {
                    // 走命令才会同步视图模型的选择与状态栏
                    Execute(_viewModel.SelectAllItemsCommand);
                    ActiveListView.Focus();
                }

                e.Handled = true;
                break;

            // Ctrl+L / Alt+D / F4 进地址栏编辑，F4 不带 Alt 才是（Alt+F4 是系统关闭）
            case Key.L when ctrl:
            case Key.D when alt:
            case Key.F4 when !alt:
                AddressBarControl.BeginEdit();
                e.Handled = true;
                break;

            case Key.X when ctrl:
                Execute(_viewModel.CutCommand);
                e.Handled = true;
                break;

            case Key.C when ctrl:
                Execute(_viewModel.CopyCommand);
                e.Handled = true;
                break;

            case Key.V when ctrl:
                Execute(_viewModel.PasteCommand);
                e.Handled = true;
                break;

            // Alt+P 已用 KeyBinding 注册，这里再兜一层，防焦点在列表里失效
            case Key.P when alt:
                Execute(_viewModel.TogglePreviewPaneCommand);
                e.Handled = true;
                break;

            case Key.F10 when shift:
            case Key.Apps:
                if (_viewModel.IsRecycleBinView)
                {
                    OpenRecycleBinContextMenuFromKeyboard();
                }
                else
                {
                    OpenContextMenuFromKeyboard();
                }

                e.Handled = true;
                break;

            // Delete 移到回收站不弹确认；Shift+Delete 永久删弹确认，回收站页面都是永久删
            case Key.Delete:
                if (_viewModel.IsRecycleBinView)
                {
                    Execute(_viewModel.DeleteRecycleBinPermanentlyCommand);
                }
                else
                {
                    Execute(shift ? _viewModel.DeletePermanentlyCommand : _viewModel.DeleteCommand);
                }

                e.Handled = true;
                break;

            case Key.Z when ctrl && !shift:
                Execute(_viewModel.UndoCommand);
                e.Handled = true;
                break;

            case Key.Y when ctrl && !shift:
                Execute(_viewModel.RedoCommand);
                e.Handled = true;
                break;
        }
    }

    private void FocusSearchBox()
    {
        try
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(MainWindow), $"聚焦搜索框失败：{ex.Message}");
        }
    }

    private static void Execute(ICommand command)
    {
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    // 坏值回退详细信息
    private static FileViewMode ParseViewMode(string? value)
        => Enum.TryParse(value, ignoreCase: true, out FileViewMode parsed) && Enum.IsDefined(parsed)
            ? parsed
            : FileViewMode.Details;

    // 主键区与小键盘数字都转成 1..8
    private static bool TryGetViewShortcutNumber(Key key, out int number)
    {
        if (key is >= Key.D1 and <= Key.D8)
        {
            number = key - Key.D0;
            return true;
        }

        if (key is >= Key.NumPad1 and <= Key.NumPad8)
        {
            number = key - Key.NumPad0;
            return true;
        }

        number = 0;
        return false;
    }

    private bool IsTextBoxFocused()
    {
        return Keyboard.FocusedElement is TextBox
               || Keyboard.FocusedElement is Wpf.Ui.Controls.TextBox;
    }

    // 地址栏

    private void OnAddressNavigateRequested(object? sender, Controls.AddressBarNavigateEventArgs e)
    {
        _viewModel.NavigateToLocation(e.Target);
    }

    private void OnSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            // 回车 = 递归搜索子文件夹，搜索框本身仍是即时筛选
            Execute(_viewModel.SearchInSubfoldersCommand);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (_viewModel.IsSearchResultView)
            {
                // 结果视图里 Esc 取消搜索并退出
                Execute(_viewModel.CancelSearchCommand);
            }
            else
            {
                _viewModel.SearchText = string.Empty;
            }

            e.Handled = true;
        }
    }

    /// <summary>搜索历史下拉：最近搜索关键词 + 清除历史</summary>
    private void OnSearchHistoryClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();

        IReadOnlyList<string> history = _viewModel.SearchHistoryItems;
        if (history.Count == 0)
        {
            var empty = new Wpf.Ui.Controls.MenuItem { Header = "（暂无搜索历史）", IsEnabled = false };
            menu.Items.Add(empty);
        }
        else
        {
            foreach (string keyword in history)
            {
                var item = new Wpf.Ui.Controls.MenuItem { Header = keyword };
                string captured = keyword;
                item.Click += (_, _) =>
                {
                    _viewModel.SearchText = captured;
                    if (_viewModel.SearchInSubfoldersCommand.CanExecute(null))
                    {
                        _viewModel.SearchInSubfoldersCommand.Execute(null);
                    }
                };

                menu.Items.Add(item);
            }

            menu.Items.Add(new Separator());
            var clear = new Wpf.Ui.Controls.MenuItem { Header = "清除历史" };
            clear.Click += (_, _) => _viewModel.ClearSearchHistoryCommand.Execute(null);
            menu.Items.Add(clear);
        }

        menu.PlacementTarget = SearchHistoryButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // 导航树

    private void OnNavigationTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is NavigationNodeViewModel node)
        {
            _viewModel.NavigateToNode(node);
        }
    }

    private void OnNavigationTreeItemExpanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { DataContext: NavigationNodeViewModel { IsExpanded: false } node })
        {
            // 用户点箭头展开时绑定可能还没回写，显式触发一次
            node.IsExpanded = true;
        }
    }

    // 视图模型交互

    private void RegisterViewModelEvents()
    {
        _viewModel.ItemFocusRequested += OnItemFocusRequested;
        _viewModel.UserMessageRequested += OnUserMessageRequested;
        _viewModel.SelectionRestoreRequested += OnSelectionRestoreRequested;
        _viewModel.CopyMoveTargetRequested += OnCopyMoveTargetRequested;
        _viewModel.PropertiesRequested += OnPropertiesRequested;
        _viewModel.NavigationRevealRequested += OnNavigationRevealRequested;
        _viewModel.RecycleBin.ToastRequested += OnRecycleBinToastRequested;
        _viewModel.RecycleBinSelectionRestoreRequested += OnRecycleBinSelectionRestoreRequested;
        _viewModel.SettingsRequested += OnSettingsRequested;

        // 新建类型 + 管理员权限相关
        _viewModel.ShortcutTargetRequested += OnShortcutTargetRequested;
        _viewModel.ElevationRequested += OnElevationRequested;
        _viewModel.RelaunchElevatedRequested += OnRelaunchElevatedRequested;

        _viewModel.PathRepairRequested += OnPathRepairRequested;

        _viewModel.SelectionApplyRequested += OnSelectionApplyRequested;
    }

    // 选择命令：全选 / 全部取消 / 反选 / 切换焦点项都在活动列表上做
    private void OnSelectionApplyRequested(object? sender, IReadOnlyList<FileItemViewModel> items)
    {
        System.Windows.Controls.ListView list = ActiveListView;

        // 批量增删会逐项发 SelectionChanged，先静音再一次性同步选择模型
        _suppressSelectionSync = true;
        try
        {
            ApplySelectionToList(list, items);
        }
        finally
        {
            _suppressSelectionSync = false;
        }

        if (items.Count > 0 && list.SelectedItems.Count > 0)
        {
            list.ScrollIntoView(list.SelectedItems[0]);
        }

        _viewModel.UpdateSelection(items);
    }

    private bool _suppressSelectionSync;

    // 大批量走 SelectAll / UnselectAll 这类原生批量接口，避免逐项改 SelectedItems
    private static void ApplySelectionToList(
        System.Windows.Controls.ListView list,
        IReadOnlyList<FileItemViewModel> items)
    {
        int total = list.Items.Count;

        if (items.Count == 0)
        {
            list.UnselectAll();
            return;
        }

        if (items.Count >= total)
        {
            list.SelectAll();
            return;
        }

        // 目标集合偏大时，全选后摘掉少数不选的，比逐个加更快
        if (items.Count > total / 2)
        {
            var wanted = new HashSet<FileItemViewModel>(items);
            list.SelectAll();

            var remove = new List<object>();
            foreach (object entry in list.SelectedItems)
            {
                if (entry is FileItemViewModel file && !wanted.Contains(file))
                {
                    remove.Add(entry);
                }
            }

            foreach (object entry in remove)
            {
                list.SelectedItems.Remove(entry);
            }

            return;
        }

        list.UnselectAll();
        foreach (FileItemViewModel item in items)
        {
            list.SelectedItems.Add(item);
        }
    }

    // 设置

    /// <summary>打开设置对话框，保存后落盘并即时生效</summary>
    private void OnSettingsRequested(object? sender, EventArgs e)
    {
        try
        {
            var viewModel = new SettingsViewModel(_settings);
            var dialog = new SettingsDialog(viewModel)
            {
                Owner = IsLoaded ? this : null,
            };

            if (dialog.ShowDialog() != true || !dialog.Saved)
            {
                return;
            }

            // 即时生效：容量写进回收站选项，删除行为写进协调器
            _viewModel.RecycleBin.ApplyCapacitySettings(_settings);
            _viewModel.RecycleBin.DeleteWithoutRecycleBin = _settings.DeleteWithoutRecycleBin;

            _services.ThemeService.SetThemeMode(_settings.ThemeMode);

            // 和关闭时保存同一份 settings.json
            _services.SettingsService.Save(_settings);

            StatusBarMessage("设置已保存并生效。");
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(MainWindow), ex);
            ShowUserMessage("无法打开设置", $"打开设置对话框失败：{ex.Message}");
        }
    }

    private void StatusBarMessage(string message)
    {
        if (!string.IsNullOrEmpty(message))
        {
            _viewModel.SetStatusMessage(message);
        }
    }

    // 删除 / 回收站

    /// <summary>删除确认对话框：模态、默认按钮「否」</summary>
    bool IDeleteConfirmation.Confirm(DeleteConfirmKind kind, IReadOnlyList<DeleteConfirmItem> items, string? detail)
    {
        try
        {
            var dialog = new ConfirmDeleteDialog(kind, items, detail)
            {
                Owner = IsLoaded ? this : null,
            };

            return dialog.ShowDialog() == true;
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(MainWindow), ex);
            return false;
        }
    }

    private void OnRecycleBinToastRequested(object? sender, RecycleBinToastEventArgs e)
    {
        try
        {
            RecycleBinToastText.Text = e.Message;
            RecycleBinToastUndoButton.Visibility = e.CanUndo ? Visibility.Visible : Visibility.Collapsed;
            RecycleBinToast.Visibility = Visibility.Visible;

            _toastTimer ??= CreateToastTimer();
            _toastTimer.Stop();
            _toastTimer.Start();
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(MainWindow), ex);
        }
    }

    private DispatcherTimer CreateToastTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(5),
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            RecycleBinToast.Visibility = Visibility.Collapsed;
        };

        return timer;
    }

    private void OnUndoDeleteClick(object sender, RoutedEventArgs e)
    {
        _toastTimer?.Stop();
        RecycleBinToast.Visibility = Visibility.Collapsed;
        Execute(_viewModel.UndoDeleteCommand);
    }

    // 只滚动不改选中，选中由视图模型设置
    private void OnNavigationRevealRequested(object? sender, NavigationNodeViewModel node)
    {
        try
        {
            Dispatcher.BeginInvoke(
                () =>
                {
                    if (NavigationTree.ItemContainerGenerator.ContainerFromItem(node) is TreeViewItem container)
                    {
                        container.BringIntoView();
                        return;
                    }

                    // 容器还没生成（深层节点）：沿父链往上找已生成的祖先再滚
                    NavigationNodeViewModel? ancestor = node;
                    while (ancestor is not null)
                    {
                        if (NavigationTree.ItemContainerGenerator.ContainerFromItem(ancestor) is TreeViewItem item)
                        {
                            item.BringIntoView();
                            return;
                        }

                        ancestor = FindParentNode(ancestor);
                    }
                },
                DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(MainWindow), $"滚动导航树失败：{ex.Message}");
        }
    }

    private NavigationNodeViewModel? FindParentNode(NavigationNodeViewModel target)
    {
        foreach (NavigationNodeViewModel root in _viewModel.NavigationRoots)
        {
            NavigationNodeViewModel? parent = FindParentNode(root, target);
            if (parent is not null)
            {
                return parent;
            }
        }

        return null;
    }

    private static NavigationNodeViewModel? FindParentNode(NavigationNodeViewModel current, NavigationNodeViewModel target)
    {
        foreach (NavigationNodeViewModel child in current.Children)
        {
            if (ReferenceEquals(child, target))
            {
                return current;
            }

            NavigationNodeViewModel? found = FindParentNode(child, target);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private void OnPropertiesRequested(object? sender, FilePropertiesViewModel viewModel)
    {
        try
        {
            var dialog = new FilePropertiesDialog(viewModel)
            {
                Owner = this,
            };

            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(MainWindow), ex);
        }
    }

    /// <summary>「复制到…/移动到…」：确认后执行并记入最近使用</summary>
    private void OnCopyMoveTargetRequested(object? sender, CopyMoveTargetRequestEventArgs e)
    {
        try
        {
            var dialog = new CopyMoveTargetDialog(e.ViewModel)
            {
                Owner = this,
            };

            if (dialog.ShowDialog() != true || string.IsNullOrEmpty(dialog.SelectedTargetPath))
            {
                return;
            }

            FileTransferResult result = _viewModel.ExecuteCopyMoveTo(e.Kind, dialog.SelectedTargetPath!);
            if (!result.Started)
            {
                // 预检没过（空间不足 / 复制到自身子目录等）
                ShowUserMessage(
                    e.Kind == Core.Operations.OperationKind.Move ? "无法移动" : "无法复制",
                    result.Message);
            }
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(MainWindow), ex);
        }
    }

    private void OnSelectionRestoreRequested(object? sender, IReadOnlyList<FileItemViewModel> items)
    {
        System.Windows.Controls.ListView list = ActiveListView;
        list.SelectedItems.Clear();

        foreach (FileItemViewModel item in items)
        {
            list.SelectedItems.Add(item);
        }

        if (items.Count > 0)
        {
            list.ScrollIntoView(items[0]);
        }

        _viewModel.UpdateSelection(list.SelectedItems.OfType<FileItemViewModel>());
    }

    private void OnItemFocusRequested(object? sender, ItemFocusEventArgs e)
    {
        SelectAndBringIntoView(e.Item);

        if (!e.BeginRename)
        {
            return;
        }

        _viewModel.BeginRename(e.Item);

        // DataTemplate 要过一轮布局才建得出编辑框
        Dispatcher.BeginInvoke(
            () => FocusRenameEditor(e.Item),
            DispatcherPriority.Loaded);
    }

    private void SelectAndBringIntoView(FileItemViewModel item)
    {
        System.Windows.Controls.ListView list = ActiveListView;
        list.SelectedItems.Clear();
        list.SelectedItem = item;
        list.ScrollIntoView(item);
        list.UpdateLayout();
    }

    private void FocusRenameEditor(FileItemViewModel item)
    {
        var container = ActiveListView.ItemContainerGenerator.ContainerFromItem(item) as ListViewItem;
        if (container is null)
        {
            LogManager.Warning(typeof(MainWindow), $"找不到重命名编辑行的容器：{item.DisplayPath}");
            return;
        }

        if (FindDescendant<TextBox>(container) is not { } editor)
        {
            LogManager.Warning(typeof(MainWindow), $"找不到重命名编辑框：{item.DisplayPath}");
            return;
        }

        editor.Focus();
        editor.SelectAll();

        // 默认只选主文件名，不含扩展名；文件夹与无扩展名项全选
        if (!item.IsDirectory)
        {
            string extension = PathHelper.GetExtension(item.Name);
            if (extension.Length > 0 && editor.Text.Length > extension.Length)
            {
                editor.Select(0, editor.Text.Length - extension.Length);
            }
        }
    }

    private void OnRenameEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox editor)
        {
            return;
        }

        if (editor.DataContext is FileItemViewModel item && item.IsRenaming)
        {
            _viewModel.ValidateEditingName(item, editor.Text);
        }
    }

    private void OnRenameEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox editor || editor.DataContext is not FileItemViewModel item)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                if (_viewModel.CommitRename(item, editor.Text))
                {
                    SelectAndBringIntoView(item);
                    ActiveListView.Focus();
                }

                break;

            case Key.Escape:
                e.Handled = true;
                _viewModel.CancelRename();
                SelectAndBringIntoView(item);
                ActiveListView.Focus();
                break;
        }
    }

    private void OnRenameEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox editor || editor.DataContext is not FileItemViewModel item || !item.IsRenaming)
        {
            return;
        }

        // 焦点移到同一行的提示 / 其它元素时忽略
        if (e.NewFocus is DependencyObject newFocus
            && FindAncestor<ListViewItem>(newFocus) is { } ancestor
            && ReferenceEquals(ancestor, ActiveListView.ItemContainerGenerator.ContainerFromItem(item)))
        {
            return;
        }

        _viewModel.CancelRename();
    }

    private void OnRenameEditorIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox editor && e.NewValue is true && editor.DataContext is FileItemViewModel { IsRenaming: true } item)
        {
            Dispatcher.BeginInvoke(() => FocusRenameEditor(item), DispatcherPriority.Input);
        }
    }

    private void OnUserMessageRequested(object? sender, UserMessageEventArgs e)
        => ShowUserMessage(e.Title, e.Message);

    private void ShowUserMessage(string title, string message)
    {
        try
        {
            var box = new Wpf.Ui.Controls.MessageBox
            {
                Title = title,
                Content = message,
                Owner = this,
                CloseButtonText = "关闭",
                IsCloseButtonEnabled = true,
                IsPrimaryButtonEnabled = false,
                IsSecondaryButtonEnabled = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };

            _ = Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await box.ShowDialogAsync(showAsDialog: false, cancellationToken: CancellationToken.None);
                }
                catch (Exception ex)
                {
                    LogManager.Error(typeof(MainWindow), ex);
                }
            });
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(MainWindow), ex);
        }
    }

    // 文件列表

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.NameColumnHeader)
            or nameof(MainViewModel.DateColumnHeader)
            or nameof(MainViewModel.TypeColumnHeader)
            or nameof(MainViewModel.SizeColumnHeader)
            or nameof(MainViewModel.SearchFolderColumnHeader)
            or nameof(MainViewModel.SortColumn)
            or nameof(MainViewModel.IsSortDescending))
        {
            UpdateColumnHeaders();
        }
        else if (e.PropertyName is nameof(MainViewModel.RecycleBinSortColumn)
                 or nameof(MainViewModel.IsRecycleBinSortDescending))
        {
            UpdateRecycleBinColumnHeaders();
        }
        else if (e.PropertyName is nameof(MainViewModel.IsCompactMode)
                 or nameof(MainViewModel.DetailsRowStyleKey))
        {
            ApplyDetailsRowStyle();
        }
        else if (e.PropertyName is nameof(MainViewModel.ShowCheckBoxes))
        {
            // 复选框开关变化后立即落盘
            _settings.ShowCheckBoxes = _viewModel.ShowCheckBoxes;
            _services.SettingsService.Save(_settings);
            UpdateColumnHeaders();
        }
        else if (e.PropertyName is nameof(MainViewModel.IsSearchResultView)
                 or nameof(MainViewModel.ShowSearchFolderColumn))
        {
            ApplySearchFolderColumn();
            UpdateColumnHeaders();
        }
        else if (e.PropertyName is nameof(MainViewModel.HeaderCheckState))
        {
            UpdateHeaderCheckBox();
        }
    }

    // 搜索视图显示「所在文件夹」列，普通目录宽度置 0；程序化改宽时抑制落盘，别脏了 settings.json
    private void ApplySearchFolderColumn()
    {
        bool show = _viewModel.ShowSearchFolderColumn;

        bool previous = _restoringColumnWidths;
        _restoringColumnWidths = true;
        try
        {
            if (show)
            {
                double? saved = _folderViews.TryGetColumnWidth(
                    AppSettings.ColumnWidthSearchPrefix + nameof(FileSortColumn.Folder));
                SearchFolderColumn.Width = saved is { } width && width >= 24 ? width : 260;

                double? savedName = _folderViews.TryGetColumnWidth(
                    AppSettings.ColumnWidthSearchPrefix + nameof(FileSortColumn.Name));
                NameColumn.Width = savedName is { } nameValue && nameValue >= 24 ? nameValue : 220;
            }
            else
            {
                SearchFolderColumn.Width = 0;

                double? nameWidth = _folderViews.TryGetColumnWidth(
                    AppSettings.ColumnWidthFilesPrefix + nameof(FileSortColumn.Name));
                NameColumn.Width = nameWidth is { } value && value >= 24 ? value : 360;
            }
        }
        finally
        {
            _restoringColumnWidths = previous;
        }
    }

    // 预览窗格

    private void OnPreviewSelectionChanged(object? sender, EventArgs e) => RefreshPreviewPane();

    private void OnPreviewPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsPreviewPaneOpen)
            or nameof(MainViewModel.IsThisPcView)
            or nameof(MainViewModel.IsRecycleBinView)
            or nameof(MainViewModel.IsSearchResultView))
        {
            RefreshPreviewPane();
        }
    }

    // 一项 → 预览；多选 → 多选状态；无 → 空状态
    private void RefreshPreviewPane()
    {
        try
        {
            if (!_viewModel.IsPreviewPaneOpen)
            {
                return;
            }

            // 回收站页面用自己的列表，预览显示空状态
            if (_viewModel.IsRecycleBinView)
            {
                PreviewPaneControl.ShowEmpty();
                return;
            }

            IReadOnlyList<FileItemViewModel> selected = _viewModel.SelectedItems;
            if (selected.Count == 0)
            {
                PreviewPaneControl.ShowEmpty();
                return;
            }

            if (selected.Count > 1)
            {
                PreviewPaneControl.ShowMultiple(selected.Count);
                return;
            }

            PreviewPaneControl.ShowTarget(_viewModel.BuildPreviewTarget());
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(MainWindow), ex);
        }
    }

    private void OnPreviewOpenRequested(object? sender, Controls.PreviewPaneOpenEventArgs e)
        => _viewModel.OpenPreviewTarget(e.Target.DisplayPath);

    private void OnPreviewCopyToDesktopRequested(object? sender, Controls.PreviewPaneCopyEventArgs e)
        => _viewModel.CopyToDesktopItems(new[] { e.Target.DisplayPath });

    private void OnPreviewRepairRequested(object? sender, Controls.PreviewPaneRepairEventArgs e)
        => _viewModel.RequestPathRepair(new[] { e.Target.DisplayPath });

    // 修复路径

    private void OnPathRepairRequested(object? sender, PathRepairRequestEventArgs e)
    {
        try
        {
            var viewModel = new PathRepairViewModel(
                e,
                _viewModel,
                _viewModel.DefaultRepairMoveTargetPath,
                Dispatcher);

            var dialog = new PathRepairDialog(viewModel, OpenFolderInExplorer)
            {
                Owner = IsLoaded ? this : null,
            };

            dialog.ShowDialog();

            // 修复可能改了当前目录内容，刷一下列表
            if (_viewModel.RefreshCommand.CanExecute(null))
            {
                _viewModel.RefreshCommand.Execute(null);
            }
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(MainWindow), ex);
            ShowUserMessage("无法打开修复路径", $"打开修复路径对话框失败：{ex.Message}");
        }
    }

    private void OpenFolderInExplorer(string folderDisplayPath)
    {
        try
        {
            if (!Services.ShellOpener.TryOpen(folderDisplayPath, out Services.ShellOpenResult result))
            {
                StatusBarMessage($"无法打开文件夹：{result.Error}");
            }
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(MainWindow), ex);
            StatusBarMessage("无法打开文件夹。");
        }
    }

    // 按紧凑模式切详细信息行高（22 / 28）
    private void ApplyDetailsRowStyle()
    {
        var style = (Style)FindResource(_viewModel.DetailsRowStyleKey);
        if (!ReferenceEquals(FileListView.ItemContainerStyle, style))
        {
            FileListView.ItemContainerStyle = style;
        }
    }

    // 列宽持久化

    // 参与列宽记忆的列，文件列表 / 搜索结果 / 回收站各自独立，键 = 视图前缀.列名
    private IEnumerable<(string Key, GridViewColumn Column)> Columns()
    {
        yield return (AppSettings.ColumnWidthFilesPrefix + nameof(FileSortColumn.Name), NameColumn);
        yield return (AppSettings.ColumnWidthSearchPrefix + nameof(FileSortColumn.Folder), SearchFolderColumn);
        yield return (AppSettings.ColumnWidthFilesPrefix + nameof(FileSortColumn.DateModified), DateColumn);
        yield return (AppSettings.ColumnWidthFilesPrefix + nameof(FileSortColumn.Type), TypeColumn);
        yield return (AppSettings.ColumnWidthFilesPrefix + nameof(FileSortColumn.Size), SizeColumn);

        yield return (AppSettings.ColumnWidthRecyclePrefix + nameof(RecycleBinSortColumn.Name), RecycleBinNameColumn);
        yield return (AppSettings.ColumnWidthRecyclePrefix + nameof(RecycleBinSortColumn.OriginalLocation), RecycleBinOriginalColumn);
        yield return (AppSettings.ColumnWidthRecyclePrefix + nameof(RecycleBinSortColumn.DeletedDate), RecycleBinDeletedColumn);
        yield return (AppSettings.ColumnWidthRecyclePrefix + nameof(RecycleBinSortColumn.Size), RecycleBinSizeColumn);
        yield return (AppSettings.ColumnWidthRecyclePrefix + nameof(RecycleBinSortColumn.Type), RecycleBinTypeColumn);
    }

    private IEnumerable<GridViewColumn> EnumerateTrackedColumns()
    {
        foreach ((_, GridViewColumn column) in Columns())
        {
            yield return column;
        }
    }

    private void RestoreColumnWidths()
    {
        _restoringColumnWidths = true;
        try
        {
            foreach ((string key, GridViewColumn column) in Columns())
            {
                double? width = _folderViews.TryGetColumnWidth(key);
                if (width is { } value && value >= 24)
                {
                    column.Width = value;
                }
            }
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(MainWindow), $"恢复列宽失败：{ex.Message}");
        }
        finally
        {
            _restoringColumnWidths = false;
        }
    }

    private void OnColumnWidthChanged(object? sender, EventArgs e)
    {
        if (_restoringColumnWidths || _isClosing || sender is not GridViewColumn column)
        {
            return;
        }

        string? key = ColumnWidthKeyFor(column);
        if (key is not null)
        {
            _folderViews.SetColumnWidth(key, column.Width);
        }
    }

    // 名称列在搜索结果视图属于 search.*，普通目录 / 回收站属于 files.*
    private string? ColumnWidthKeyFor(GridViewColumn column)
    {
        if (ReferenceEquals(column, NameColumn))
        {
            string prefix = _viewModel.IsSearchResultView
                ? AppSettings.ColumnWidthSearchPrefix
                : AppSettings.ColumnWidthFilesPrefix;
            return prefix + nameof(FileSortColumn.Name);
        }

        foreach ((string key, GridViewColumn candidate) in Columns())
        {
            if (ReferenceEquals(candidate, column))
            {
                return key;
            }
        }

        return null;
    }

    // 列头文字写进 GridView 列：排序箭头叠在标题上方居中，非排序列只有文字
    private void UpdateColumnHeaders()
    {
        NameColumn.Header = BuildNameHeaderContent();
        DateColumn.Header = BuildHeaderContent(_viewModel.DateColumnHeader, FileSortColumn.DateModified);
        TypeColumn.Header = BuildHeaderContent(_viewModel.TypeColumnHeader, FileSortColumn.Type);
        SizeColumn.Header = BuildHeaderContent(_viewModel.SizeColumnHeader, FileSortColumn.Size);

        // 「所在文件夹」列只在搜索结果视图显示
        SearchFolderColumn.Header = _viewModel.ShowSearchFolderColumn
            ? BuildHeaderContent(_viewModel.SearchFolderColumnHeader, FileSortColumn.Folder)
            : string.Empty;

        UpdateHeaderCheckBox();
    }

    // 名称列头：复选框模式下最左侧挂全选框
    private UIElement BuildNameHeaderContent()
    {
        UIElement title = BuildHeaderContent(_viewModel.NameColumnHeader, FileSortColumn.Name);
        if (!_viewModel.ShowCheckBoxes)
        {
            _headerCheckBox = null;
            return title;
        }

        _headerCheckBox = new CheckBox
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            IsThreeState = true,
            Focusable = false,
        };

        _headerCheckBox.SetValue(AutomationProperties.AutomationIdProperty, "HeaderCheckBox");
        _headerCheckBox.SetValue(AutomationProperties.NameProperty, "全选");
        _headerCheckBox.Click += OnHeaderCheckBoxClick;

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(_headerCheckBox);
        panel.Children.Add(title);
        return panel;
    }

    private CheckBox? _headerCheckBox;

    private bool _updatingHeaderCheckBox;

    // 表头全选框：全选 / 全不选，部分选中显示半选
    private void OnHeaderCheckBoxClick(object sender, RoutedEventArgs e)
    {
        // 别让点击冒泡到列头触发排序
        e.Handled = true;

        if (_updatingHeaderCheckBox || sender is not CheckBox)
        {
            return;
        }

        // 半选或未选都点成全选，已全选则清空，跟资源管理器一致
        if (_viewModel.HeaderCheckState == true)
        {
            Execute(_viewModel.ClearSelectionCommand);
        }
        else
        {
            Execute(_viewModel.SelectAllItemsCommand);
        }

        UpdateHeaderCheckBox();
    }

    private void UpdateHeaderCheckBox()
    {
        if (_headerCheckBox is null)
        {
            return;
        }

        _updatingHeaderCheckBox = true;
        try
        {
            _headerCheckBox.IsChecked = _viewModel.HeaderCheckState;
        }
        finally
        {
            _updatingHeaderCheckBox = false;
        }
    }

    // 标题与箭头叠在同一格，箭头不占布局高度，保证所有列标题同一基线
    private UIElement BuildHeaderContent(string title, FileSortColumn column)
    {
        var text = new TextBlock
        {
            Text = title,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // 单格 Grid：标题与箭头重叠互不撑高；格子左对齐收缩到标题宽，箭头 Center 即居中于文字上方
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        grid.Children.Add(text);

        // 排序箭头只当前排序列显示，叠在标题上方居中
        if (_viewModel.SortColumn == column)
        {
            var arrow = new TextBlock
            {
                Text = _viewModel.IsSortDescending ? "▼" : "▲",
                FontSize = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
            };

            // 用 DynamicResource 才能随主题即时更新，FindResource 会缓存画刷
            arrow.SetResourceReference(TextBlock.ForegroundProperty, "CjTextSecondaryBrush");
            grid.Children.Add(arrow);
        }

        return grid;
    }

    private void OnColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header || header.Role == GridViewColumnHeaderRole.Padding)
        {
            return;
        }

        // 回收站页面用另一套排序列
        if (sender is System.Windows.Controls.ListView { Name: "RecycleBinListView" })
        {
            OnRecycleBinColumnHeaderClick(header);
            return;
        }

        if (FileListView.View is not GridView gridView)
        {
            return;
        }

        // 用列对象比对（不用固定索引），避免列顺序调整后错位
        GridViewColumn? column = header.Column;
        string? sortColumn =
            ReferenceEquals(column, NameColumn) ? nameof(FileSortColumn.Name) :
            ReferenceEquals(column, SearchFolderColumn) ? nameof(FileSortColumn.Folder) :
            ReferenceEquals(column, DateColumn) ? nameof(FileSortColumn.DateModified) :
            ReferenceEquals(column, TypeColumn) ? nameof(FileSortColumn.Type) :
            ReferenceEquals(column, SizeColumn) ? nameof(FileSortColumn.Size) :
            null;

        if (sortColumn is not null)
        {
            _viewModel.SortByColumnCommand.Execute(sortColumn);
        }
    }

    private void OnRecycleBinColumnHeaderClick(GridViewColumnHeader header)
    {
        if (RecycleBinListView.View is not GridView gridView)
        {
            return;
        }

        int index = gridView.Columns.IndexOf(header.Column);
        string? column = index switch
        {
            0 => nameof(RecycleBinSortColumn.Name),
            1 => nameof(RecycleBinSortColumn.OriginalLocation),
            2 => nameof(RecycleBinSortColumn.DeletedDate),
            3 => nameof(RecycleBinSortColumn.Size),
            4 => nameof(RecycleBinSortColumn.Type),
            _ => null,
        };

        if (column is not null)
        {
            _viewModel.SortRecycleBinByColumnCommand.Execute(column);
        }
    }

    // 「此电脑」页面

    private void OnThisPcFolderDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ThisPcFolderList.SelectedItem is ThisPcFolderViewModel folder)
        {
            _viewModel.ActivateThisPcFolder(folder);
            e.Handled = true;
        }
    }

    private void OnThisPcDriveDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ThisPcDriveList.SelectedItem is ThisPcDriveViewModel drive)
        {
            _viewModel.ActivateThisPcDrive(drive);
            e.Handled = true;
        }
    }

    // 方向键交给 ListBox 默认处理
    private void OnThisPcKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not System.Windows.Controls.ListBox list)
        {
            return;
        }

        switch (list.SelectedItem)
        {
            case ThisPcFolderViewModel folder:
                _viewModel.ActivateThisPcFolder(folder);
                break;

            case ThisPcDriveViewModel drive:
                _viewModel.ActivateThisPcDrive(drive);
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    private void OnFileListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 批量设置选择期间不逐项同步，结束后由调用方统一更新一次
        if (_suppressSelectionSync)
        {
            return;
        }

        if (sender is System.Windows.Controls.ListView list)
        {
            _viewModel.UpdateSelection(list.SelectedItems.OfType<FileItemViewModel>());
        }
    }

    // Ctrl+Space：只切换焦点项，其它已选项保持不动
    private void ToggleFocusedItemSelection()
    {
        if (_viewModel.IsRecycleBinView)
        {
            return;
        }

        System.Windows.Controls.ListView list = ActiveListView;

        // 焦点可能在未选中的行上，先取焦点行，取不到再用当前选中项
        FileItemViewModel? item = Keyboard.FocusedElement is DependencyObject focused
            && FindAncestor<ListViewItem>(focused) is { DataContext: FileItemViewModel focusedItem }
            ? focusedItem
            : list.SelectedItem as FileItemViewModel;

        if (item is null)
        {
            return;
        }

        if (list.SelectedItems.Contains(item))
        {
            list.SelectedItems.Remove(item);
        }
        else
        {
            list.SelectedItems.Add(item);
        }

        // 取消选中会清掉 ListView 的焦点锚点，把键盘焦点按回该行，方便连续切换
        if (list.ItemContainerGenerator.ContainerFromItem(item) is ListViewItem container)
        {
            container.Focus();
        }

        _viewModel.UpdateSelection(list.SelectedItems.OfType<FileItemViewModel>());
    }

    // 复选框：按下即切换该行，拦住默认行为以免整行选择跟着变
    private void OnItemCheckBoxPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_applyingCheckBox || sender is not CheckBox box)
        {
            return;
        }

        e.Handled = true;
        ApplyCheckBoxSelection(box, select: box.IsChecked != true);
    }

    private bool _applyingCheckBox;

    private void ApplyCheckBoxSelection(CheckBox box, bool select)
    {
        if (FindAncestor<ListViewItem>(box) is not { } container
            || container.DataContext is not FileItemViewModel item
            || FindAncestor<System.Windows.Controls.ListView>(container) is not { } list)
        {
            return;
        }

        _applyingCheckBox = true;
        try
        {
            if (select)
            {
                if (!list.SelectedItems.Contains(item))
                {
                    list.SelectedItems.Add(item);
                }
            }
            else
            {
                list.SelectedItems.Remove(item);
            }
        }
        finally
        {
            _applyingCheckBox = false;
        }

        _viewModel.UpdateSelection(list.SelectedItems.OfType<FileItemViewModel>());
    }

    // Ctrl+单击已选中项 = 取消选中，WPF 的 ListView 默认不取消
    private void OnFileListPreviewMouseLeftButtonDownToggle(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListView list
            || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        var hitItem = FindAncestor<System.Windows.Controls.ListViewItem>(e.OriginalSource as DependencyObject);
        bool nameCellOnly = Controls.RubberBandSelector.GetNameCellOnly(list);
        bool onNameContent = Controls.ListHitTest.IsNameContentHit(e.OriginalSource as DependencyObject);

        // 复选框自己处理，别当成 Ctrl+单击取消选中
        if (Controls.ListHitTest.IsItemChromeHit(e.OriginalSource as DependencyObject))
        {
            return;
        }

        // 只处理点在条目上的情况；NameCellOnly 模式要求点在名称内容上
        if (hitItem?.DataContext is not FileItemViewModel item)
        {
            return;
        }

        if (nameCellOnly && !onNameContent)
        {
            return;
        }

        if (list.SelectedItems.Contains(item))
        {
            // 自己处理取消选中，并阻止默认的「再次选中」
            list.SelectedItems.Remove(item);
            e.Handled = true;
            _viewModel.UpdateSelection(list.SelectedItems.OfType<FileItemViewModel>());
        }
    }

    // 回收站页面

    private void OnRecycleBinSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ListView list)
        {
            _viewModel.UpdateRecycleBinSelection(list.SelectedItems.OfType<RecycleBinItemViewModel>());
        }
    }

    private void OnRecycleBinMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject) is null)
        {
            return;
        }

        Execute(_viewModel.RestoreRecycleBinCommand);
        e.Handled = true;
    }

    private void OnRecycleBinKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Execute(_viewModel.RestoreRecycleBinCommand);
                e.Handled = true;
                break;

            case Key.Delete:
                Execute(_viewModel.DeleteRecycleBinPermanentlyCommand);
                e.Handled = true;
                break;
        }
    }

    private void UpdateRecycleBinColumnHeaders()
    {
        RecycleBinNameColumn.Header = BuildRecycleBinHeaderContent(
            _viewModel.RecycleBinNameColumnHeader, RecycleBinSortColumn.Name);
        RecycleBinOriginalColumn.Header = BuildRecycleBinHeaderContent(
            _viewModel.RecycleBinOriginalLocationColumnHeader, RecycleBinSortColumn.OriginalLocation);
        RecycleBinDeletedColumn.Header = BuildRecycleBinHeaderContent(
            _viewModel.RecycleBinDeletedColumnHeader, RecycleBinSortColumn.DeletedDate);
        RecycleBinSizeColumn.Header = BuildRecycleBinHeaderContent(
            _viewModel.RecycleBinSizeColumnHeader, RecycleBinSortColumn.Size);
        RecycleBinTypeColumn.Header = BuildRecycleBinHeaderContent(
            _viewModel.RecycleBinTypeColumnHeader, RecycleBinSortColumn.Type);
    }

    private UIElement BuildRecycleBinHeaderContent(string title, RecycleBinSortColumn column)
    {
        var text = new TextBlock
        {
            Text = title,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        grid.Children.Add(text);

        if (_viewModel.RecycleBinSortColumn == column)
        {
            var arrow = new TextBlock
            {
                Text = _viewModel.IsRecycleBinSortDescending ? "▼" : "▲",
                FontSize = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
            };

            // 用 DynamicResource 才能随主题即时更新，FindResource 会缓存画刷
            arrow.SetResourceReference(TextBlock.ForegroundProperty, "CjTextSecondaryBrush");
            grid.Children.Add(arrow);
        }

        return grid;
    }

    // 排序后恢复回收站页面的选中项
    private void OnRecycleBinSelectionRestoreRequested(object? sender, IReadOnlyList<RecycleBinItemViewModel> items)
    {
        RecycleBinListView.SelectedItems.Clear();
        foreach (RecycleBinItemViewModel item in items)
        {
            RecycleBinListView.SelectedItems.Add(item);
        }

        _viewModel.UpdateRecycleBinSelection(RecycleBinListView.SelectedItems.OfType<RecycleBinItemViewModel>());
    }

    // 图标列表与详细信息共用同一个选择模型
    private void OnIconListSelectionChanged(object sender, SelectionChangedEventArgs e)
        => OnFileListSelectionChanged(sender, e);

    private void OnFileListMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 只在真正双击到某一项时打开，空白处不处理
        if (FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject) is null)
        {
            return;
        }

        _viewModel.Activate((sender as System.Windows.Controls.ListView)?.SelectedItem as FileItemViewModel);
        e.Handled = true;
    }

    private void OnFileListKeyDown(object sender, KeyEventArgs e)
    {
        // 焦点在名称编辑框里时按键都算文本输入，不在这里处理
        if (e.OriginalSource is DependencyObject source
            && FindAncestor<TextBox>(source) is not null)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            _viewModel.OpenSelectedCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F2)
        {
            Execute(_viewModel.RenameCommand);
            e.Handled = true;
            return;
        }

        // 网格视图方向键二维移动：左右 ±1，上下 ±每行列数
        if (TryMoveSelectionInGrid(e))
        {
            e.Handled = true;
            return;
        }

        // 输入字母 / 数字跳到该前缀第一项，连续输入累积前缀，1 秒重置
        if (TryTypeAhead(e))
        {
            e.Handled = true;
        }
    }

    // 类型跳转：可打印字符累积成前缀，定位列表第一项
    private bool TryTypeAhead(KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0)
        {
            return false;
        }

        string? text = KeyToText(e.Key);
        if (text is null)
        {
            return false;
        }

        DateTime now = DateTime.UtcNow;
        if (now - _typeAheadLastInput > TypeAheadResetTimeout)
        {
            _typeAheadBuffer.Clear();
        }

        _typeAheadLastInput = now;
        _typeAheadBuffer.Append(text);

        FileItemViewModel? match = _viewModel.FindByPrefix(_typeAheadBuffer.ToString());
        if (match is null)
        {
            return false;
        }

        SelectAndBringIntoView(match);
        _viewModel.UpdateSelection(ActiveListView.SelectedItems.OfType<FileItemViewModel>());
        return true;
    }

    // 只认字母与数字，免得跟导航键冲突
    private static string? KeyToText(Key key)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            char c = (char)('a' + (key - Key.A));
            return Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? char.ToUpperInvariant(c).ToString() : c.ToString();
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            return ((char)('0' + (key - Key.D0))).ToString();
        }

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            return ((char)('0' + (key - Key.NumPad0))).ToString();
        }

        return null;
    }

    // 新建类型 / 管理员权限

    /// <summary>「新建快捷方式」弹目标输入框，取消返回 null</summary>
    private string? OnShortcutTargetRequested()
        => ShortcutTargetDialog.Ask(IsLoaded ? this : null);

    private void OnElevationRequested(object? sender, ElevationRequestEventArgs e)
    {
        try
        {
            ElevationChoice choice = ElevationRequiredDialog.Ask(
                IsLoaded ? this : null, e.FolderDisplayPath, e.AllowApplyToAll, out bool applyToAll);

            MainViewModel.ElevationAnswer answer = choice switch
            {
                ElevationChoice.Relaunch => MainViewModel.ElevationAnswer.Relaunch,
                ElevationChoice.Skip => MainViewModel.ElevationAnswer.Skip,
                _ => MainViewModel.ElevationAnswer.Cancel,
            };

            _viewModel.SetElevationAnswer(e, answer, applyToAll);
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(MainWindow), ex);
            _viewModel.SetElevationAnswer(e, MainViewModel.ElevationAnswer.Cancel, applyToAll: false);
        }
    }

    private void OnRelaunchElevatedRequested(string folderDisplayPath)
    {
        try
        {
            if (ElevationService.TryRelaunchElevated(folderDisplayPath, out string error))
            {
                StatusBarMessage("已打开管理员窗口。");
            }
            else if (!string.IsNullOrEmpty(error))
            {
                StatusBarMessage($"无法以管理员身份重新打开：{error}");
            }
            // 用户取消 UAC：error 为空，静默回原状态
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(MainWindow), ex);
            StatusBarMessage("无法以管理员身份重新打开。");
        }
    }

    // 可视化树深度优先找第一个指定类型子元素，用于在单元格里找编辑框
    private static T? FindDescendant<T>(DependencyObject? root)
        where T : DependencyObject
    {
        if (root is null)
        {
            return null;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            T? descendant = FindDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    // 窗口位置 / 设置

    private void RestoreWindowPlacement()
    {
        WindowPlacement placement = _settings.Window;

        // 宽高按 DIP 保存，切换 DPI 后视觉大小一致；这里只收敛上下限，别恢复出比工作区还大的窗口
        double maxWidth = Math.Max(960, SystemParameters.WorkArea.Width);
        double maxHeight = Math.Max(600, SystemParameters.WorkArea.Height);

        if (!placement.HasValue)
        {
            // 首次启动：默认尺寸 + 居中（XAML 已设 CenterScreen）
            Width = Math.Clamp(placement.Width, 960, maxWidth);
            Height = Math.Clamp(placement.Height, 600, maxHeight);
            return;
        }

        Width = Math.Clamp(placement.Width, 960, maxWidth);
        Height = Math.Clamp(placement.Height, 600, maxHeight);

        if (IsPlacementOnScreen(placement.Left, placement.Top, Width, Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = placement.Left;
            Top = placement.Top;
        }
        else
        {
            // 位置已超出当前屏幕（显示器被拔掉等），回默认居中
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            LogManager.Warning(
                typeof(MainWindow),
                $"保存的窗口位置不在当前屏幕内（{placement.Left},{placement.Top}），已回退为居中显示。");
        }

        if (placement.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        // 有进行中的复制 / 移动任务：先问是否取消并退出（异步问，避免阻塞 UI 线程）
        if (!_exitConfirmed && _viewModel.Coordinator.HasActiveTasks)
        {
            e.Cancel = true;

            bool exit = await ConfirmExitWithActiveTasksAsync();
            if (exit)
            {
                _exitConfirmed = true;

                // 进关闭流程：置关闭标志（后台回调不再碰界面）、取消任务，等引擎结束再真关
                _viewModel.Coordinator.BeginShutdown();
                await _viewModel.Coordinator.WaitForIdleAsync(TimeSpan.FromSeconds(10));

                Close();
            }

            return;
        }

        // 句柄销毁前得先取消主题监视，否则 UnWatch 取不到 HWND 会失败
        _services.ThemeService.UnwatchWindow(this);
        UnregisterClipboardListener();
        StopDeviceWatcher();
        CaptureAndSaveSettings();

        // 临时副本被外部改过就提示另存回原位置（防丢数据）；异步框要 UI 线程，同步阻塞会死锁，故先 e.Cancel 再 await
        if (!_tempCopyPrompted && TempOpenCopy.ActiveCopies.Any(TempOpenCopy.IsModified))
        {
            e.Cancel = true;
            _tempCopyPrompted = true;
            await PromptModifiedTempCopiesAsync();
            Close();
            return;
        }

        // 删短根联接和临时副本（异常退出由 ProcessExit / App.OnExit 兜底）
        LongPathOpenCleanup();
    }

    private async Task<bool> ConfirmExitWithActiveTasksAsync()
    {
        try
        {
            var box = new Wpf.Ui.Controls.MessageBox
            {
                Title = "仍有任务正在进行",
                Content = "仍有复制或移动任务正在进行。退出将取消这些任务（已完成的文件不受影响）。是否取消任务并退出？",
                Owner = this,
                PrimaryButtonText = "取消任务并退出",
                CloseButtonText = "返回",
                IsCloseButtonEnabled = true,
            };

            Wpf.Ui.Controls.MessageBoxResult result =
                await box.ShowDialogAsync(showAsDialog: true, cancellationToken: CancellationToken.None);

            return result == Wpf.Ui.Controls.MessageBoxResult.Primary;
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(MainWindow), ex);
            return true;
        }
    }

    private void CaptureAndSaveSettings()
    {
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;

        try
        {
            // 最大化时记 RestoreBounds，还原尺寸才对
            Rect bounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;

            _settings.Window = new WindowPlacement
            {
                HasValue = true,
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height,
                IsMaximized = WindowState == WindowState.Maximized,
            };

            _settings.NavigationWidth = _viewModel.NavigationWidth;
            _settings.PreviewPaneWidth = _viewModel.PreviewPaneWidth;
            _settings.IsPreviewPaneOpen = _viewModel.IsPreviewPaneOpen;
            _settings.ViewMode = _viewModel.ViewMode.ToString();
            _settings.IconSize = _viewModel.IconSize;
            _settings.IsCompactMode = _viewModel.IsCompactMode;
            _settings.ShowCheckBoxes = _viewModel.ShowCheckBoxes;

            _services.SettingsService.Save(_settings);

            // 「每文件夹视图记忆」的最近使用时间也一并落盘（视图 / 排序变化时已即时写过）
            _folderViews.Save();
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(MainWindow), ex);
        }
    }

    // 判断保存的窗口位置是否落在虚拟桌面内（覆盖所有显示器），有交集即算可见
    private static bool IsPlacementOnScreen(double left, double top, double width, double height)
    {
        if (double.IsNaN(left) || double.IsNaN(top)
            || double.IsInfinity(left) || double.IsInfinity(top))
        {
            return false;
        }

        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        return virtualScreen.IntersectsWith(new Rect(left, top, width, height));
    }
}
