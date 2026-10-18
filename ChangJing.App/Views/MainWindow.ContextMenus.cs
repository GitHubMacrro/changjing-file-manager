using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ChangJing.App.Services;
using ChangJing.App.ViewModels;
using Wpf.Ui.Controls;
using UiMenuItem = Wpf.Ui.Controls.MenuItem;

namespace ChangJing.App.Views;

/// <summary>MainWindow 的右键菜单部分</summary>
public partial class MainWindow
{
    private FileItemViewModel? _contextTarget;

    private bool _contextOnItem;

    private bool _recycleBinContextOnItem;

    private bool _openingOwnContextMenu;

    private System.Windows.Controls.ListView ActiveListView
        => _viewModel.IsDetailsView ? FileListView : IconListView;

    private void RegisterContextMenus()
    {
        foreach (System.Windows.Controls.ListView list in new System.Windows.Controls.ListView[] { FileListView, IconListView })
        {
            list.PreviewMouseRightButtonDown += OnFileListPreviewMouseRightButtonDown;
            list.PreviewMouseRightButtonUp += OnFileListPreviewMouseRightButtonUp;
            list.ContextMenuOpening += OnFileListContextMenuOpening;
        }

        RecycleBinListView.PreviewMouseRightButtonDown += OnRecycleBinPreviewMouseRightButtonDown;
        RecycleBinListView.PreviewMouseRightButtonUp += OnRecycleBinPreviewMouseRightButtonUp;
        RecycleBinListView.ContextMenuOpening += OnFileListContextMenuOpening;

        foreach (System.Windows.Controls.ListBox list in new[] { ThisPcFolderList, ThisPcDriveList })
        {
            list.PreviewMouseRightButtonUp += OnThisPcPreviewMouseRightButtonUp;
            list.ContextMenuOpening += OnFileListContextMenuOpening;
        }
    }

    // 回收站菜单

    private void OnRecycleBinPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<System.Windows.Controls.ListViewItem>(e.OriginalSource as DependencyObject)?.DataContext
            as RecycleBinItemViewModel;

        if (item is null)
        {
            _recycleBinContextOnItem = false;
            return;
        }

        if (!RecycleBinListView.SelectedItems.Contains(item))
        {
            RecycleBinListView.SelectedItems.Clear();
            RecycleBinListView.SelectedItem = item;
        }

        _recycleBinContextOnItem = true;
    }

    private void OnRecycleBinPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShowRecycleBinContextMenu(Mouse.GetPosition(RecycleBinListView));
    }

    private void OpenRecycleBinContextMenuFromKeyboard()
    {
        _recycleBinContextOnItem = RecycleBinListView.SelectedItem is RecycleBinItemViewModel;
        Point anchor = _recycleBinContextOnItem
            ? new Point(0, 0)
            : new Point(RecycleBinListView.ActualWidth / 2, RecycleBinListView.ActualHeight / 2);

        ShowRecycleBinContextMenu(anchor);
    }

    private void ShowRecycleBinContextMenu(Point position)
    {
        ContextMenu menu = BuildRecycleBinContextMenu();
        if (menu.Items.Count == 0)
        {
            return;
        }

        menu.PlacementTarget = RecycleBinListView;
        menu.Placement = PlacementMode.RelativePoint;
        menu.HorizontalOffset = position.X;
        menu.VerticalOffset = position.Y;

        _openingOwnContextMenu = true;
        try
        {
            menu.IsOpen = true;
        }
        finally
        {
            _openingOwnContextMenu = false;
        }
    }

    private ContextMenu BuildRecycleBinContextMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(CreateItem(
            "还原",
            SymbolRegular.ArrowUndo20,
            _viewModel.RestoreRecycleBinCommand,
            enabled: _viewModel.HasRecycleBinSelection()));
        menu.Items.Add(CreateItem(
            "永久删除",
            SymbolRegular.DeleteLines20,
            _viewModel.DeleteRecycleBinPermanentlyCommand,
            enabled: _viewModel.HasRecycleBinSelection()));

        menu.Items.Add(new Separator());

        menu.Items.Add(CreateItem(
            "清空回收站",
            SymbolRegular.Broom20,
            _viewModel.EmptyRecycleBinCommand,
            enabled: _viewModel.CanEmptyRecycleBin()));

        menu.Items.Add(new Separator());

        menu.Items.Add(CreateItem("刷新", SymbolRegular.ArrowSync20, _viewModel.RefreshCommand, "F5"));

        return menu;
    }

    private void OnFileListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_openingOwnContextMenu)
        {
            return;
        }

        e.Handled = true;
    }

    private void OnFileListPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListView list)
        {
            return;
        }

        (bool onItem, FileItemViewModel? target, bool isBackground) =
            ResolveContextTarget(list, e.OriginalSource as DependencyObject);

        ApplyContextTarget(list, onItem, target, isBackground);
    }

    /// <summary>按右键命中更新列表选择与目标</summary>
    internal void ApplyContextTarget(
        System.Windows.Controls.ListView list,
        bool onItem,
        FileItemViewModel? target,
        bool isBackground)
    {
        if (!onItem || target is null)
        {
            _contextTarget = null;
            _contextOnItem = false;

            if (isBackground)
            {
                list.SelectedItems.Clear();
            }

            return;
        }

        if (!list.SelectedItems.Contains(target))
        {
            list.SelectedItems.Clear();
            list.SelectedItem = target;
        }

        _contextTarget = target;
        _contextOnItem = true;
    }

    /// <summary>解析右键命中：是否命中项、命中哪项、是否背景</summary>
    internal (bool OnItem, FileItemViewModel? Target, bool IsBackground) ResolveContextTarget(
        System.Windows.Controls.ListView list,
        DependencyObject? source)
    {
        var hitItem = FindAncestor<System.Windows.Controls.ListViewItem>(source);
        bool nameCellOnly = Controls.RubberBandSelector.GetNameCellOnly(list);
        bool onNameContent = Controls.ListHitTest.IsNameContentHit(source);

        // 行内复选框等控件上右键，按命中该项处理，别当空白清掉选择
        bool onChrome = Controls.ListHitTest.IsItemChromeHit(source);

        // NameCellOnly 视图下，行内但不在名称内容上算背景
        bool background = hitItem is null || (nameCellOnly && !onNameContent && !onChrome);
        if (background)
        {
            return (false, null, hitItem is null || nameCellOnly);
        }

        var item = hitItem!.DataContext as FileItemViewModel;
        return item is null ? (false, null, true) : (true, item, false);
    }

    private void OnFileListPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 右键拖动刚弹过菜单，这里别再弹项菜单
        if (_rightDragJustCompleted)
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        ShowContextMenu(Mouse.GetPosition(ActiveListView));
    }

    private void OnThisPcPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        UIElement target = sender as UIElement ?? ActiveListView;
        ShowThisPcRefreshMenu(target, Mouse.GetPosition(target));
    }

    private void ShowThisPcRefreshMenu(UIElement target, Point position)
    {
        ContextMenu menu = BuildThisPcBlankMenu();
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.RelativePoint;
        menu.HorizontalOffset = position.X;
        menu.VerticalOffset = position.Y;

        _openingOwnContextMenu = true;
        try
        {
            menu.IsOpen = true;
        }
        finally
        {
            _openingOwnContextMenu = false;
        }
    }

    private void OpenContextMenuFromKeyboard()
    {
        _contextTarget = ActiveListView.SelectedItem as FileItemViewModel;
        _contextOnItem = _contextTarget is not null;

        Point anchor = _contextOnItem
            ? new Point(0, 0)
            : new Point(ActiveListView.ActualWidth / 2, ActiveListView.ActualHeight / 2);

        ShowContextMenu(anchor);
    }

    private void ShowContextMenu(Point position)
    {
        ContextMenu menu = BuildContextMenu();
        if (menu.Items.Count == 0)
        {
            return;
        }

        menu.PlacementTarget = ActiveListView;
        menu.Placement = PlacementMode.RelativePoint;
        menu.HorizontalOffset = position.X;
        menu.VerticalOffset = position.Y;

        // HwndHost 空域会遮住菜单，弹出前先藏起来
        menu.Opened += (_, _) => PreviewPaneControl.SetSystemPreviewHostVisible(false);
        menu.Closed += (_, _) => PreviewPaneControl.SetSystemPreviewHostVisible(true);


        _openingOwnContextMenu = true;
        try
        {
            menu.IsOpen = true;
        }
        finally
        {
            _openingOwnContextMenu = false;
        }
    }

    private ContextMenu BuildContextMenu()
    {
        if (_contextOnItem && _contextTarget is not null)
        {
            return _viewModel.IsThisPcView ? BuildDriveMenu() : BuildItemMenu();
        }

        return _viewModel.IsThisPcView ? BuildThisPcBlankMenu() : BuildBlankMenu();
    }

    // 项菜单

    private ContextMenu BuildItemMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(CreateItem(
            "打开", SymbolRegular.Open20, _viewModel.OpenSelectedCommand, "Enter",
            enabled: _viewModel.CanOpenSelection()));
        menu.Items.Add(BuildOpenWithSubmenu());

        menu.Items.Add(new Separator());

        menu.Items.Add(CreateItem("剪切", SymbolRegular.Cut20, _viewModel.CutCommand, "Ctrl+X",
            enabled: _viewModel.CanCut()));
        menu.Items.Add(CreateItem("复制", SymbolRegular.Copy20, _viewModel.CopyCommand, "Ctrl+C",
            enabled: _viewModel.CanCopy()));
        menu.Items.Add(CreateItem("复制路径", SymbolRegular.Link20, _viewModel.CopyPathCommand,
            "Ctrl+Shift+C", enabled: _viewModel.CanCopyPath()));

        menu.Items.Add(new Separator());

        menu.Items.Add(CreateItem("复制到…", SymbolRegular.DocumentCopy20, _viewModel.CopyToCommand,
            enabled: _viewModel.CanCopyOrMoveTo()));
        menu.Items.Add(CreateItem("移动到…", SymbolRegular.ArrowMove20, _viewModel.MoveToCommand,
            enabled: _viewModel.CanCopyOrMoveTo()));

        menu.Items.Add(new Separator());

        menu.Items.Add(CreateItem("删除", SymbolRegular.Delete20, _viewModel.DeleteCommand, "Delete",
            enabled: _viewModel.HasSelection()));
        menu.Items.Add(CreateItem("永久删除", SymbolRegular.DeleteLines20, _viewModel.DeletePermanentlyCommand, "Shift+Delete",
            enabled: _viewModel.HasSelection()));
        menu.Items.Add(CreateItem("重命名", SymbolRegular.Rename20, _viewModel.RenameCommand, "F2",
            enabled: _viewModel.CanRename()));

        menu.Items.Add(new Separator());

        if (_viewModel.IsSearchResultView)
        {
            menu.Items.Add(CreateItem("打开所在文件夹", SymbolRegular.FolderOpen20, _viewModel.OpenContainingFolderCommand,
                enabled: _viewModel.CanOpenContainingFolder()));
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(CreateItem(
            "修复路径…", SymbolRegular.Wrench20, _viewModel.RepairPathsCommand,
            enabled: _viewModel.CanRepairPaths()));
        menu.Items.Add(CreateItem("属性", SymbolRegular.Info20, _viewModel.ShowPropertiesCommand, "Alt+Enter"));

        return menu;
    }

    private UiMenuItem BuildOpenWithSubmenu()
    {
        var submenu = new UiMenuItem
        {
            Header = "打开方式",
            Icon = new SymbolIcon { Symbol = SymbolRegular.Open20 },
            IsEnabled = _viewModel.CanOpenWith(),
        };

        if (_viewModel.CanOpenWith())
        {
            foreach (OpenWithHandler handler in _viewModel.EnumerateOpenWithHandlers())
            {
                string executable = handler.Command;
                UiMenuItem item = CreateItem(handler.DisplayName, null, command: null);
                item.Click += (_, _) => _viewModel.OpenWithProgram(executable);
                submenu.Items.Add(item);
            }

            if (submenu.Items.Count > 0)
            {
                submenu.Items.Add(new Separator());
            }
        }

        UiMenuItem openAs = CreateItem("选择其他应用…", null, command: null, enabled: _viewModel.CanOpenWith());
        openAs.Click += (_, _) => _viewModel.OpenWithDialog();
        submenu.Items.Add(openAs);

        return submenu;
    }

    private ContextMenu BuildDriveMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(CreateItem(
            "打开", SymbolRegular.Open20, _viewModel.OpenSelectedCommand, "Enter",
            enabled: _viewModel.CanOpenSelection()));
        return menu;
    }

    // 空白菜单

    private ContextMenu BuildBlankMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(BuildViewSubmenu());
        menu.Items.Add(BuildSortSubmenu());

        menu.Items.Add(BuildGroupSubmenu());

        menu.Items.Add(new Separator());

        SelectionMenuItems selection = PopulateSelectionItems(menu);

        // 打开时按当前选中项重算可用状态
        menu.Opened += (_, _) => RefreshSelectionItems(selection);

        menu.Items.Add(new Separator());

        menu.Items.Add(CreateItem("刷新", SymbolRegular.ArrowSync20, _viewModel.RefreshCommand, "F5"));

        menu.Items.Add(new Separator());

        if (_viewModel.CanUndoOperation())
        {
            menu.Items.Add(CreateItem(
                _viewModel.UndoMenuText, SymbolRegular.ArrowUndo20, _viewModel.UndoCommand, "Ctrl+Z"));
        }

        if (_viewModel.CanRedoOperation())
        {
            menu.Items.Add(CreateItem(
                _viewModel.RedoMenuText, SymbolRegular.ArrowRedo20, _viewModel.RedoCommand, "Ctrl+Y"));
        }

        if (_viewModel.CanUndoOperation() || _viewModel.CanRedoOperation())
        {
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(CreateItem(
            "粘贴", SymbolRegular.ClipboardPaste20, _viewModel.PasteCommand, "Ctrl+V",
            enabled: _viewModel.CanPaste()));

        if (_viewModel.CanCreateFolder())
        {
            menu.Items.Add(BuildNewSubmenu());
        }

        menu.Items.Add(new Separator());

        menu.Items.Add(CreateItem(
            "属性",
            SymbolRegular.Info20,
            _viewModel.ShowPropertiesCommand,
            "Alt+Enter",
            enabled: _viewModel.CanShowProperties()));
        return menu;
    }

    private UiMenuItem BuildNewSubmenu()
    {
        var submenu = new UiMenuItem
        {
            Header = "新建",
            Icon = new SymbolIcon { Symbol = SymbolRegular.Add20 },
            IsEnabled = _viewModel.CanCreateFolder(),
        };

        PopulateNewItems(submenu);
        return submenu;
    }

    private ContextMenu BuildNewContextMenu()
    {
        var menu = new ContextMenu();
        PopulateNewItems(menu);

        // ShellNew 类型是后台加载的，打开时重刷一次
        menu.Opened += (_, _) => PopulateNewItems(menu);
        return menu;
    }

    private void PopulateNewItems(ItemsControl target)
    {
        target.Items.Clear();

        bool separatorInserted = false;
        foreach (NewMenuItem entry in _viewModel.NewItems)
        {
            if (entry.IsSeparator)
            {
                target.Items.Add(new Separator());
                separatorInserted = true;
                continue;
            }

            if (entry.Kind == NewMenuItemKind.ShellNew && !separatorInserted)
            {
                target.Items.Add(new Separator());
                separatorInserted = true;
            }

            UiMenuItem item = CreateItem(entry.DisplayName, null, command: null,
                enabled: _viewModel.CanCreateFolder());

            if (entry.Kind == NewMenuItemKind.Folder)
            {
                item.Icon = new SymbolIcon { Symbol = SymbolRegular.Folder20 };
                item.InputGestureText = "Ctrl+Shift+N";
            }
            else if (entry.Kind == NewMenuItemKind.Shortcut)
            {
                item.Icon = new SymbolIcon { Symbol = SymbolRegular.Link20 };
            }
            else if (entry.Icon is not null)
            {
                item.Icon = new ImageIcon { Source = entry.Icon };
            }

            NewMenuItem captured = entry;
            item.Click += (_, _) => _viewModel.CreateNewItem(captured);
            target.Items.Add(item);
        }
    }

    private ContextMenu BuildThisPcBlankMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(CreateItem("刷新", SymbolRegular.ArrowSync20, _viewModel.RefreshCommand, "F5"));
        return menu;
    }

    // 排序 / 查看

    private sealed class ViewMenuItems
    {
        public Dictionary<FileViewMode, UiMenuItem> Modes { get; } = new();

        public UiMenuItem? CompactMode;

        public UiMenuItem? CheckBoxes;
    }

    private UiMenuItem BuildViewSubmenu()
    {
        var submenu = new UiMenuItem
        {
            Header = "查看",
            Icon = new SymbolIcon { Symbol = SymbolRegular.Grid20 },
        };

        PopulateViewItems(submenu);
        SelectionMenuItems selection = PopulateSelectionItems(submenu);

        // 展开时按当前选中项重算可用状态
        submenu.SubmenuOpened += (_, _) => RefreshSelectionItems(selection);
        return submenu;
    }

    private UiMenuItem BuildSortSubmenu()
    {
        var submenu = new UiMenuItem
        {
            Header = "排序方式",
            Icon = new SymbolIcon { Symbol = SymbolRegular.ArrowSort20 },
        };

        PopulateSortItems(submenu);
        return submenu;
    }

    // 分组依据

    private sealed class GroupMenuItems
    {
        public UiMenuItem? Name;
        public UiMenuItem? DateModified;
        public UiMenuItem? Type;
        public UiMenuItem? Size;
        public UiMenuItem? Folder;
        public UiMenuItem? None;
        public UiMenuItem? Ascending;
        public UiMenuItem? Descending;
    }

    private UiMenuItem BuildGroupSubmenu()
    {
        var submenu = new UiMenuItem
        {
            Header = "分组依据",
            Icon = new SymbolIcon { Symbol = SymbolRegular.GroupList20 },
        };

        // 空白菜单每次重建，勾选状态天然是新的
        PopulateGroupItems(submenu);
        return submenu;
    }

    private GroupMenuItems PopulateGroupItems(ItemsControl target)
    {
        var items = new GroupMenuItems
        {
            Name = CreateItem("名称", null, _viewModel.SetGroupByCommand, parameter: "Name",
                isCheckable: true, isChecked: _viewModel.IsGroupByName),
            DateModified = CreateItem("修改日期", null, _viewModel.SetGroupByCommand, parameter: "DateModified",
                isCheckable: true, isChecked: _viewModel.IsGroupByDateModified),
            Type = CreateItem("类型", null, _viewModel.SetGroupByCommand, parameter: "Type",
                isCheckable: true, isChecked: _viewModel.IsGroupByType),
            Size = CreateItem("大小", null, _viewModel.SetGroupByCommand, parameter: "Size",
                isCheckable: true, isChecked: _viewModel.IsGroupBySize),
        };

        target.Items.Add(items.Name);
        target.Items.Add(items.DateModified);
        target.Items.Add(items.Type);
        target.Items.Add(items.Size);

        if (_viewModel.ShowSearchFolderColumn)
        {
            items.Folder = CreateItem("所在文件夹", null, _viewModel.SetGroupByCommand, parameter: "Folder",
                isCheckable: true, isChecked: _viewModel.IsGroupByFolder);
            target.Items.Add(items.Folder);
        }

        items.None = CreateItem("（无）", null, _viewModel.SetGroupByCommand, parameter: "None",
            isCheckable: true, isChecked: _viewModel.IsGroupByNone);
        target.Items.Add(items.None);

        target.Items.Add(new Separator());

        items.Ascending = CreateItem("升序", null, _viewModel.SetGroupAscendingCommand,
            isCheckable: true, isChecked: _viewModel.IsGroupAscending);
        items.Descending = CreateItem("降序", null, _viewModel.SetGroupDescendingCommand,
            isCheckable: true, isChecked: _viewModel.IsGroupDescending);
        target.Items.Add(items.Ascending);
        target.Items.Add(items.Descending);

        return items;
    }

    private void RefreshGroupChecks(GroupMenuItems items)
    {
        SetChecked(items.Name, _viewModel.IsGroupByName);
        SetChecked(items.DateModified, _viewModel.IsGroupByDateModified);
        SetChecked(items.Type, _viewModel.IsGroupByType);
        SetChecked(items.Size, _viewModel.IsGroupBySize);
        SetChecked(items.Folder, _viewModel.IsGroupByFolder);
        SetChecked(items.None, _viewModel.IsGroupByNone);
        SetChecked(items.Ascending, _viewModel.IsGroupAscending);
        SetChecked(items.Descending, _viewModel.IsGroupDescending);
    }

    private ContextMenu BuildSortContextMenu()    {
        var menu = new ContextMenu();
        SortMenuItems items = PopulateSortItems(menu);

        // 打开时只刷新勾选，别在 Opened 里改项集合
        menu.Opened += (_, _) => RefreshSortChecks(items);
        return menu;
    }

    private ContextMenu BuildViewContextMenu()
    {
        var menu = new ContextMenu();
        ViewMenuItems items = PopulateViewItems(menu);
        SelectionMenuItems selection = PopulateSelectionItems(menu);

        menu.Opened += (_, _) =>
        {
            RefreshViewChecks(items);
            RefreshSelectionItems(selection);
        };

        return menu;
    }

    // 选择命令的菜单项，Opened 时要刷新可用状态
    private sealed class SelectionMenuItems
    {
        public UiMenuItem? SelectAll;
        public UiMenuItem? Clear;
        public UiMenuItem? Invert;
    }

    private ViewMenuItems PopulateViewItems(ItemsControl target)
    {
        var items = new ViewMenuItems();

        foreach (FileViewMode mode in FileViewModeInfo.ShortcutOrder)
        {
            int number = FileViewModeInfo.ToShortcutNumber(mode);
            UiMenuItem item = CreateItem(
                FileViewModeInfo.GetMenuLabel(mode),
                ViewModeIcon(mode),
                _viewModel.SetViewModeCommand,
                gesture: $"Ctrl+Shift+{number}",
                isCheckable: true,
                isChecked: _viewModel.IsViewMode(mode),
                parameter: mode.ToString());

            target.Items.Add(item);
            items.Modes[mode] = item;
        }

        // 紧凑模式：详细信息行高 22
        target.Items.Add(new Separator());
        items.CompactMode = CreateItem(
            "紧凑模式",
            SymbolRegular.AppsList20,
            _viewModel.ToggleCompactModeCommand,
            isCheckable: true,
            isChecked: _viewModel.IsCompactMode);

        // 项目复选框
        items.CheckBoxes = CreateItem(
            "项目复选框",
            SymbolRegular.CheckboxChecked20,
            _viewModel.ToggleCheckBoxesCommand,
            isCheckable: true,
            isChecked: _viewModel.ShowCheckBoxes);

        return items;
    }

    private void RefreshViewChecks(ViewMenuItems items)
    {
        foreach (KeyValuePair<FileViewMode, UiMenuItem> pair in items.Modes)
        {
            pair.Value.IsChecked = _viewModel.IsViewMode(pair.Key);
        }

        SetChecked(items.CompactMode, _viewModel.IsCompactMode);
        SetChecked(items.CheckBoxes, _viewModel.ShowCheckBoxes);
    }

    // 选择命令：空白右键菜单与「查看」下拉共用
    private SelectionMenuItems PopulateSelectionItems(ItemsControl target)
    {
        var items = new SelectionMenuItems
        {
            SelectAll = CreateItem(
                "全选", SymbolRegular.SelectAllOn20, _viewModel.SelectAllItemsCommand, "Ctrl+A",
                enabled: _viewModel.CanSelectAll()),
            Clear = CreateItem(
                "全部取消", SymbolRegular.SelectAllOff20, _viewModel.ClearSelectionCommand, "Esc",
                enabled: _viewModel.CanClearSelection()),
            Invert = CreateItem(
                "反向选择", SymbolRegular.ArrowSwap20, _viewModel.InvertSelectionCommand,
                enabled: _viewModel.CanInvertSelection()),
        };

        target.Items.Add(new Separator());
        target.Items.Add(items.SelectAll);
        target.Items.Add(items.Clear);
        target.Items.Add(items.Invert);
        return items;
    }

    // 选择随选中项变化，菜单打开时重算可用状态
    private void RefreshSelectionItems(SelectionMenuItems items)
    {
        if (items.SelectAll is not null)
        {
            items.SelectAll.IsEnabled = _viewModel.CanSelectAll();
        }

        if (items.Clear is not null)
        {
            items.Clear.IsEnabled = _viewModel.CanClearSelection();
        }

        if (items.Invert is not null)
        {
            items.Invert.IsEnabled = _viewModel.CanInvertSelection();
        }
    }

    private static SymbolRegular ViewModeIcon(FileViewMode mode) => mode switch
    {
        FileViewMode.ExtraLargeIcons => SymbolRegular.Grid20,
        FileViewMode.LargeIcons => SymbolRegular.Grid20,
        FileViewMode.MediumIcons => SymbolRegular.Grid20,
        FileViewMode.SmallIcons => SymbolRegular.Grid20,
        FileViewMode.List => SymbolRegular.AppsListDetail20,
        FileViewMode.Details => SymbolRegular.AppsList20,
        FileViewMode.Tiles => SymbolRegular.AppsListDetail20,
        FileViewMode.Content => SymbolRegular.TextBulletListSquare20,
        _ => SymbolRegular.Grid20,
    };

    private sealed class SortMenuItems
    {
        public UiMenuItem? Name;
        public UiMenuItem? Folder;
        public UiMenuItem? DateModified;
        public UiMenuItem? Type;
        public UiMenuItem? Size;
        public UiMenuItem? Ascending;
        public UiMenuItem? Descending;
    }

    private SortMenuItems PopulateSortItems(ItemsControl target)
    {
        var items = new SortMenuItems
        {
            Name = CreateItem("名称", null, _viewModel.SortByMenuColumnCommand, parameter: "Name",
                isCheckable: true, isChecked: _viewModel.IsSortByName),
            DateModified = CreateItem("修改日期", null, _viewModel.SortByMenuColumnCommand, parameter: "DateModified",
                isCheckable: true, isChecked: _viewModel.IsSortByDateModified),
            Type = CreateItem("类型", null, _viewModel.SortByMenuColumnCommand, parameter: "Type",
                isCheckable: true, isChecked: _viewModel.IsSortByType),
            Size = CreateItem("大小", null, _viewModel.SortByMenuColumnCommand, parameter: "Size",
                isCheckable: true, isChecked: _viewModel.IsSortBySize),
            Ascending = CreateItem("升序", null, _viewModel.SetSortAscendingCommand,
                isCheckable: true, isChecked: _viewModel.IsSortAscending),
            Descending = CreateItem("降序", null, _viewModel.SetSortDescendingCommand,
                isCheckable: true, isChecked: _viewModel.IsSortDescending),
        };

        target.Items.Add(items.Name);
        target.Items.Add(items.DateModified);
        target.Items.Add(items.Type);
        target.Items.Add(items.Size);

        if (_viewModel.ShowSearchFolderColumn)
        {
            items.Folder = CreateItem("所在文件夹", null, _viewModel.SortByMenuColumnCommand, parameter: "Folder",
                isCheckable: true, isChecked: _viewModel.IsSortByFolder);
            target.Items.Add(items.Folder);
        }

        target.Items.Add(new Separator());

        target.Items.Add(items.Ascending);
        target.Items.Add(items.Descending);

        return items;
    }

    private void RefreshSortChecks(SortMenuItems items)
    {
        SetChecked(items.Name, _viewModel.IsSortByName);
        SetChecked(items.Folder, _viewModel.IsSortByFolder);
        SetChecked(items.DateModified, _viewModel.IsSortByDateModified);
        SetChecked(items.Type, _viewModel.IsSortByType);
        SetChecked(items.Size, _viewModel.IsSortBySize);
        SetChecked(items.Ascending, _viewModel.IsSortAscending);
        SetChecked(items.Descending, _viewModel.IsSortDescending);
    }

    private static void SetChecked(UiMenuItem? item, bool value)
    {
        if (item is not null)
        {
            item.IsChecked = value;
        }
    }

    // 构建辅助

    private static UiMenuItem CreateItem(
        string header,
        SymbolRegular? icon,
        ICommand? command,
        string? gesture = null,
        bool enabled = true,
        bool isCheckable = false,
        bool isChecked = false,
        object? parameter = null)
    {
        var item = new UiMenuItem
        {
            Header = header,
            IsEnabled = enabled,
            IsCheckable = isCheckable,
            IsChecked = isChecked,
        };

        if (icon is not null)
        {
            item.Icon = new SymbolIcon { Symbol = icon.Value };
        }

        if (!string.IsNullOrEmpty(gesture))
        {
            item.InputGestureText = gesture!;
        }

        if (command is not null)
        {
            item.Command = command;
            item.CommandParameter = parameter;
        }

        return item;
    }
}
