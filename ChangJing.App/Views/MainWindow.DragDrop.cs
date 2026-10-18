using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ChangJing.App.Controls;
using ChangJing.App.Core.DragDrop;
using ChangJing.App.Core.LongPath;
using ChangJing.App.Services;
using ChangJing.App.ViewModels;
using CustomWin.Utils.Logs;
using UiMenuItem = Wpf.Ui.Controls.MenuItem;

namespace ChangJing.App.Views;

/// <summary>MainWindow 的拖放部分</summary>
public partial class MainWindow
{
    private Point _dragOrigin;

    private System.Windows.Controls.ListView? _dragSourceList;

    private bool _dragStarted;

    private bool _rightDragJustCompleted;

    private bool _rightDrag;

    private IReadOnlyList<string> _dragInternalSources = Array.Empty<string>();

    private UIElement? _highlightElement;
    private DropHighlightAdorner? _highlightAdorner;
    private DragFeedbackAdorner? _feedbackAdorner;

    private string _currentDropTarget = string.Empty;

    private DispatcherTimer? _autoExpandTimer;
    private NavigationNodeViewModel? _autoExpandNode;

    private DispatcherTimer? _autoScrollTimer;
    private int _autoScrollDelta;

    private Point _lastDragPoint;

    private void RegisterDragDrop()
    {
        foreach (System.Windows.Controls.ListView list in new[] { FileListView, IconListView })
        {
            list.AllowDrop = true;
            list.PreviewMouseLeftButtonDown += OnDragSourcePreviewMouseLeftButtonDown;
            list.PreviewMouseRightButtonDown += OnDragSourcePreviewMouseRightButtonDown;
            list.PreviewMouseMove += OnDragSourcePreviewMouseMove;
            list.DragOver += OnListDragOver;
            list.DragLeave += (_, _) => ClearDropHighlight();
            list.Drop += OnListDrop;
        }

        // 回收站页可拖出但不可作为放置目标
        RecycleBinListView.AllowDrop = false;
        RecycleBinListView.PreviewMouseLeftButtonDown += OnRecycleBinDragSourceMouseLeftButtonDown;
        RecycleBinListView.PreviewMouseMove += OnRecycleBinDragSourceMouseMove;

        NavigationTree.AllowDrop = true;
        NavigationTree.DragOver += OnTreeDragOver;
        NavigationTree.DragLeave += (_, _) => ClearDropHighlight();
        NavigationTree.Drop += OnTreeDrop;

        foreach (System.Windows.Controls.ListBox list in new[] { ThisPcFolderList, ThisPcDriveList })
        {
            list.AllowDrop = true;
            list.DragOver += OnThisPcDragOver;
            list.DragLeave += (_, _) => ClearDropHighlight();
            list.Drop += OnThisPcDrop;
        }
    }

    // 拖动起点

    private void OnDragSourcePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => CaptureDragOrigin(sender, e, rightDrag: false);

    private void OnDragSourcePreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        => CaptureDragOrigin(sender, e, rightDrag: true);

    private void CaptureDragOrigin(object sender, MouseButtonEventArgs e, bool rightDrag)
    {
        _dragStarted = false;
        _rightDrag = rightDrag;
        _dragOrigin = e.GetPosition(null);
        _dragSourceList = sender as System.Windows.Controls.ListView;
        _dragInternalSources = Array.Empty<string>();
    }

    private void OnDragSourcePreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStarted || _dragSourceList is null)
        {
            return;
        }

        bool buttonHeld = _rightDrag
            ? e.RightButton == MouseButtonState.Pressed
            : e.LeftButton == MouseButtonState.Pressed;
        if (!buttonHeld)
        {
            return;
        }

        Point current = e.GetPosition(null);
        if (Math.Abs(current.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        // 空白处按下留给橡皮筋框选；复选框等行内控件不拖动
        var hitItem = FindAncestor<System.Windows.Controls.ListViewItem>(e.OriginalSource as DependencyObject);
        if (hitItem?.DataContext is not FileItemViewModel
            || Controls.ListHitTest.IsItemChromeHit(e.OriginalSource as DependencyObject))
        {
            return;
        }

        // NameCellOnly 视图下，非名称内容算背景不拖动
        if (_dragSourceList is { } sourceList
            && Controls.RubberBandSelector.GetNameCellOnly(sourceList)
            && !Controls.ListHitTest.IsNameContentHit(e.OriginalSource as DependencyObject))
        {
            return;
        }

        _dragStarted = true;
        StartInternalDrag(_dragSourceList);
    }

    private void StartInternalDrag(System.Windows.Controls.ListView list)
    {        IReadOnlyList<FileItemViewModel> selected = list.SelectedItems.OfType<FileItemViewModel>().ToArray();
        if (selected.Count == 0)
        {
            return;
        }

        var sources = selected
            .Where(item => !item.IsDrive)
            .Select(item => item.DisplayPath)
            .ToList();

        if (sources.Count == 0)
        {
            return;
        }

        _dragInternalSources = sources;

        DragDropService.DragOutData data = DragDropService.BuildDragOutData(sources);
        if (data.Data is null)
        {
            if (data.HasSkipped)
            {
                ShowUserMessage("无法拖出", "此项目路径过长，无法拖到其他程序。");
            }

            _dragInternalSources = Array.Empty<string>();
            return;
        }

        if (data.HasSkipped)
        {
            StatusBarMessage($"有 {data.SkippedCount} 个项目路径过长，无法拖到其他程序，已只拖出其余项目。");
        }

        // 拖出只允许复制，防止外部程序移走源文件
        DragDropEffects allowed = DragDropEffects.Copy;

        try
        {
            DragDrop.DoDragDrop(list, data.Data, allowed);
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(MainWindow), ex);
        }
        finally
        {
            if (_rightDrag)
            {
                _rightDragJustCompleted = true;
                Dispatcher.BeginInvoke(() => _rightDragJustCompleted = false, DispatcherPriority.Input);
            }

            _dragInternalSources = Array.Empty<string>();
            _rightDrag = false;
            ClearDropHighlight();
            StopAutoScroll();
            StopAutoExpand();
        }
    }

    // 放置目标判定

    private void OnRecycleBinDragSourceMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStarted = false;
        _rightDrag = false;
        _dragOrigin = e.GetPosition(null);
        _dragSourceList = RecycleBinListView;
        _dragInternalSources = Array.Empty<string>();
    }

    private void OnRecycleBinDragSourceMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStarted || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Point current = e.GetPosition(null);
        if (Math.Abs(current.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (FindAncestor<System.Windows.Controls.ListViewItem>(e.OriginalSource as DependencyObject)?.DataContext
            is not RecycleBinItemViewModel)
        {
            return;
        }

        _dragStarted = true;

        // 拖出的是回收站里的本体副本，不动原文件
        var sources = RecycleBinListView.SelectedItems.OfType<RecycleBinItemViewModel>()
            .Select(item => item.Entry.ItemPath)
            .Where(path => !string.IsNullOrEmpty(path))
            .ToList();

        if (sources.Count == 0)
        {
            return;
        }

        _dragInternalSources = sources;

        DragDropService.DragOutData data = DragDropService.BuildDragOutData(sources);
        if (data.Data is null)
        {
            StatusBarMessage("此项目路径过长，无法拖到其他程序。");
            _dragInternalSources = Array.Empty<string>();
            return;
        }

        try
        {
            DragDrop.DoDragDrop(RecycleBinListView, data.Data, DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(MainWindow), ex);
        }
        finally
        {
            _dragInternalSources = Array.Empty<string>();
            _dragStarted = false;
        }
    }

    private string ResolveListDropTarget(System.Windows.Controls.ListView list, DependencyObject? source)
    {
        var hitItem = FindAncestor<System.Windows.Controls.ListViewItem>(source);
        if (hitItem?.DataContext is FileItemViewModel { IsDirectory: true } folder)
        {
            return folder.DisplayPath;
        }

        return _viewModel.IsThisPcView || _viewModel.IsRecycleBinView ? string.Empty : _viewModel.CurrentLocationDisplay;
    }

    private static string ResolveTreeDropTarget(DependencyObject? source)
    {
        var hitItem = FindAncestor<TreeViewItem>(source);
        return hitItem?.DataContext is NavigationNodeViewModel { CanNavigate: true } node && node.DisplayPath is not null
            ? node.DisplayPath
            : string.Empty;
    }

    // 列表 DragOver / Drop

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListView list)
        {
            return;
        }

        string target = ResolveListDropTarget(list, e.OriginalSource as DependencyObject);
        HandleDragOverCore(e, target, FindAncestor<System.Windows.Controls.ListViewItem>(e.OriginalSource as DependencyObject));
        HandleAutoScroll(list, e);
    }

    private void OnListDrop(object sender, DragEventArgs e)
        => HandleDropCore(e, _currentDropTarget);

    // 导航树 DragOver / Drop

    private void OnTreeDragOver(object sender, DragEventArgs e)
    {
        if (!_viewModel.CanAcceptDrop)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        string target = ResolveTreeDropTarget(e.OriginalSource as DependencyObject);
        var container = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        HandleDragOverCore(e, target, container);
        HandleTreeAutoExpand(container);
    }

    private void OnTreeDrop(object sender, DragEventArgs e)
        => HandleDropCore(e, _currentDropTarget);

    // 此电脑页 DragOver / Drop

    private void OnThisPcDragOver(object sender, DragEventArgs e)
    {
        string target = string.Empty;
        object? data = FindAncestor<System.Windows.Controls.ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext;

        target = data switch
        {
            ThisPcFolderViewModel folder when folder.IsAvailable => folder.DisplayPath,
            ThisPcDriveViewModel { IsReady: true } drive => drive.RootPath,
            _ => string.Empty,
        };

        HandleDragOverCore(e, target, FindAncestor<System.Windows.Controls.ListBoxItem>(e.OriginalSource as DependencyObject));
    }

    private void OnThisPcDrop(object sender, DragEventArgs e)
        => HandleDropCore(e, _currentDropTarget);

    // 共用处理

    private void HandleDragOverCore(DragEventArgs e, string targetFolder, UIElement? highlightTarget)
    {
        IReadOnlyList<string> sources = _dragInternalSources.Count > 0
            ? _dragInternalSources
            : DragDropService.ReadFileDrop(e.Data);

        if (!_viewModel.CanAcceptDrop || string.IsNullOrEmpty(targetFolder) || sources.Count == 0)
        {
            e.Effects = DragDropEffects.None;
            _currentDropTarget = string.Empty;
            ClearDropHighlight();
            ShowDragFeedback(null);
            e.Handled = true;
            return;
        }

        bool ctrl = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
        bool shift = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0;
        bool alt = (e.KeyStates & DragDropKeyStates.AltKey) != 0;

        DropDecision decision = _viewModel.ResolveDrop(sources, targetFolder, ctrl, shift, alt);

        if (decision.Forbidden)
        {
            e.Effects = DragDropEffects.None;
            _currentDropTarget = string.Empty;
            ClearDropHighlight();
            ShowDragFeedback(decision.Tooltip);
            e.Handled = true;
            return;
        }

        _currentDropTarget = targetFolder;
        e.Effects = ToEffects(decision.Action);

        if (_rightDrag)
        {
            // 右键拖动悬停时允许所有效果，松开再弹菜单
            e.Effects = DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link;
        }

        HighlightTarget(highlightTarget);
        ShowDragFeedback(decision.Tooltip);
        e.Handled = true;
    }

    private void HandleDropCore(DragEventArgs e, string targetFolder)
    {
        e.Handled = true;

        IReadOnlyList<string> sources = _dragInternalSources.Count > 0
            ? _dragInternalSources
            : DragDropService.ReadFileDrop(e.Data);

        StopAutoScroll();
        StopAutoExpand();
        ClearDropHighlight();
        ShowDragFeedback(null);

        if (sources.Count == 0 || string.IsNullOrEmpty(targetFolder))
        {
            e.Effects = DragDropEffects.None;
            _rightDrag = false;
            return;
        }

        bool ctrl = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
        bool shift = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0;
        bool alt = (e.KeyStates & DragDropKeyStates.AltKey) != 0;

        if (_rightDrag)
        {
            // 右键拖动不立即执行，交给菜单决定
            e.Effects = DragDropEffects.None;
            _rightDrag = false;
            ShowRightDragMenu(sources, targetFolder);
            return;
        }

        string message = _viewModel.PerformDrop(sources, targetFolder, ctrl, shift, alt);
        e.Effects = ToEffects(_viewModel.ResolveDrop(sources, targetFolder, ctrl, shift, alt).Action);
        if (!string.IsNullOrEmpty(message))
        {
            StatusBarMessage(message);
        }
    }

    private static DragDropEffects ToEffects(DropAction action) => action switch
    {
        DropAction.Move => DragDropEffects.Move,
        DropAction.Copy => DragDropEffects.Copy,
        DropAction.Shortcut => DragDropEffects.Link,
        _ => DragDropEffects.None,
    };

    // 高亮 / 反馈

    private void HighlightTarget(UIElement? element)
    {
        if (ReferenceEquals(_highlightElement, element))
        {
            return;
        }

        ClearDropHighlight();
        if (element is null)
        {
            return;
        }

        _highlightElement = element;
        _highlightAdorner = new DropHighlightAdorner(element, AccentColor(), AccentFillColor());

        AdornerLayer? layer = AdornerLayer.GetAdornerLayer(element);
        layer?.Add(_highlightAdorner);
    }

    private void ClearDropHighlight()
    {
        if (_highlightAdorner is not null && _highlightElement is not null)
        {
            AdornerLayer.GetAdornerLayer(_highlightElement)?.Remove(_highlightAdorner);
        }

        _highlightAdorner = null;
        _highlightElement = null;
    }

    private void ShowDragFeedback(string? text)
    {
        AdornerLayer? layer = AdornerLayer.GetAdornerLayer(this);
        if (layer is null)
        {
            return;
        }

        if (string.IsNullOrEmpty(text))
        {
            if (_feedbackAdorner is not null)
            {
                layer.Remove(_feedbackAdorner);
                _feedbackAdorner = null;
            }

            return;
        }

        if (_feedbackAdorner is null)
        {
            _feedbackAdorner = new DragFeedbackAdorner(this);
            layer.Add(_feedbackAdorner);
        }

        _feedbackAdorner.Text = text!;
        _feedbackAdorner.Position = Mouse.GetPosition(this);
    }

    private Color AccentColor()
    {
        if (TryFindResource("CjAccentColor") is Color color)
        {
            return color;
        }

        return Colors.DodgerBlue;
    }

    private Color AccentFillColor()
    {
        Color accent = AccentColor();
        return Color.FromArgb(0x30, accent.R, accent.G, accent.B);
    }

    // 自动滚动 / 自动展开

    private void HandleAutoScroll(System.Windows.Controls.ListView list, DragEventArgs e)
    {
        _lastDragPoint = e.GetPosition(list);

        if (FindScrollViewer(list) is not { } scroll)
        {
            return;
        }

        const double margin = 24;
        double y = _lastDragPoint.Y;
        double height = list.ActualHeight;

        int delta = 0;
        if (y < margin)
        {
            delta = -18;
        }
        else if (y > height - margin)
        {
            delta = 18;
        }

        if (delta == 0)
        {
            StopAutoScroll();
            return;
        }

        _autoScrollDelta = delta;
        if (_autoScrollTimer is null)
        {
            _autoScrollTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(60),
            };
            _autoScrollTimer.Tick += (_, _) =>
            {
                if (_scrollTarget is not null)
                {
                    _scrollTarget.ScrollToVerticalOffset(_scrollTarget.VerticalOffset + _autoScrollDelta);
                }
            };
        }

        _scrollTarget = scroll;
        if (!_autoScrollTimer.IsEnabled)
        {
            _autoScrollTimer.Start();
        }
    }

    private ScrollViewer? _scrollTarget;

    private void StopAutoScroll()
    {
        if (_autoScrollTimer is not null)
        {
            _autoScrollTimer.Stop();
            _autoScrollTimer = null;
        }

        _scrollTarget = null;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            ScrollViewer? found = FindScrollViewer(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private void HandleTreeAutoExpand(TreeViewItem? container)
    {
        if (container?.DataContext is not NavigationNodeViewModel { CanExpand: true, IsExpanded: false } node)
        {
            StopAutoExpand();
            return;
        }

        if (ReferenceEquals(_autoExpandNode, node))
        {
            return;
        }

        StopAutoExpand();
        _autoExpandNode = node;

        _autoExpandTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };

        _autoExpandTimer.Tick += (_, _) =>
        {
            StopAutoExpand();
            if (node.CanExpand)
            {
                node.IsExpanded = true;
            }
        };

        _autoExpandTimer.Start();
    }

    private void StopAutoExpand()
    {
        if (_autoExpandTimer is not null)
        {
            _autoExpandTimer.Stop();
            _autoExpandTimer = null;
        }

        _autoExpandNode = null;
    }

    // 右键拖动菜单

    private void ShowRightDragMenu(IReadOnlyList<string> sources, string targetFolder)
    {
        string name = PathHelper.GetName(PathHelper.ToDisplay(targetFolder));
        if (string.IsNullOrEmpty(name))
        {
            name = "目标文件夹";
        }

        var menu = new ContextMenu { PlacementTarget = this, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };

        menu.Items.Add(CreateDragMenuItem($"复制到此处", sources, targetFolder, DropAction.Copy));
        menu.Items.Add(CreateDragMenuItem($"移动到此处", sources, targetFolder, DropAction.Move));
        menu.Items.Add(CreateDragMenuItem($"在此处创建快捷方式", sources, targetFolder, DropAction.Shortcut));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateDragMenuItem("取消", sources, targetFolder, DropAction.None, isCancel: true));

        _ = name;

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

    private UiMenuItem CreateDragMenuItem(
        string header,
        IReadOnlyList<string> sources,
        string targetFolder,
        DropAction action,
        bool isCancel = false)
    {
        var item = new UiMenuItem { Header = header };
        item.Click += (_, _) =>
        {
            if (isCancel)
            {
                return;
            }

            string message = action switch
            {
                DropAction.Move => _viewModel.PerformDrop(sources, targetFolder, ctrl: false, shift: true, alt: false),
                DropAction.Copy => _viewModel.PerformDrop(sources, targetFolder, ctrl: true, shift: false, alt: false),
                DropAction.Shortcut => _viewModel.PerformDrop(sources, targetFolder, ctrl: false, shift: false, alt: true),
                _ => string.Empty,
            };

            if (!string.IsNullOrEmpty(message))
            {
                StatusBarMessage(message);
            }
        };

        return item;
    }
}
