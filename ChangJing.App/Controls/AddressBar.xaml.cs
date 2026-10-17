using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ChangJing.App.Core.FileSystem;
using ChangJing.App.Core.LongPath;
using ChangJing.App.Services;

namespace ChangJing.App.Controls;

public sealed class AddressBarNavigateEventArgs : EventArgs
{
    public AddressBarNavigateEventArgs(string target) => Target = target;

    /// <summary>导航目标：普通路径或「此电脑」标识</summary>
    public string Target { get; }
}

/// <summary>资源管理器风格的面包屑地址栏</summary>
// 输入都经 PathHelper.ToDisplay，界面上永不含 \\?\
public partial class AddressBar : UserControl
{
    public event EventHandler<AddressBarNavigateEventArgs>? NavigateRequested;

    private readonly List<BreadcrumbSegment> _segments = new();
    private readonly List<List<UIElement>> _segmentElements = new();

    private BreadcrumbSegment[] _collapsedSegments = Array.Empty<BreadcrumbSegment>();
    private bool _isEditing;
    private int _popupGeneration;

    public AddressBar()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateCollapseState();
        Loaded += (_, _) => UpdateCollapseState();
    }

    // 依赖属性

    /// <summary>当前位置：普通路径或「此电脑」标识</summary>
    public static readonly DependencyProperty LocationProperty = DependencyProperty.Register(
        nameof(Location), typeof(string), typeof(AddressBar),
        new PropertyMetadata(string.Empty, OnLocationChanged));

    /// <summary>当前位置：普通路径或「此电脑」标识</summary>
    public string Location
    {
        get => (string)GetValue(LocationProperty);
        set => SetValue(LocationProperty, value);
    }

    public static readonly DependencyProperty ThisPcNameProperty = DependencyProperty.Register(
        nameof(ThisPcName), typeof(string), typeof(AddressBar),
        new PropertyMetadata("此电脑", OnLocationChanged));

    public string ThisPcName
    {
        get => (string)GetValue(ThisPcNameProperty);
        set => SetValue(ThisPcNameProperty, value);
    }

    public static readonly DependencyProperty ThisPcTokenProperty = DependencyProperty.Register(
        nameof(ThisPcToken), typeof(string), typeof(AddressBar),
        new PropertyMetadata("::ThisPC", OnLocationChanged));

    public string ThisPcToken
    {
        get => (string)GetValue(ThisPcTokenProperty);
        set => SetValue(ThisPcTokenProperty, value);
    }

    public static readonly DependencyProperty RecycleBinNameProperty = DependencyProperty.Register(
        nameof(RecycleBinName), typeof(string), typeof(AddressBar),
        new PropertyMetadata("回收站", OnLocationChanged));

    public string RecycleBinName
    {
        get => (string)GetValue(RecycleBinNameProperty);
        set => SetValue(RecycleBinNameProperty, value);
    }

    public static readonly DependencyProperty RecycleBinTokenProperty = DependencyProperty.Register(
        nameof(RecycleBinToken), typeof(string), typeof(AddressBar),
        new PropertyMetadata("::RecycleBin", OnLocationChanged));

    public string RecycleBinToken
    {
        get => (string)GetValue(RecycleBinTokenProperty);
        set => SetValue(RecycleBinTokenProperty, value);
    }

    public bool IsEditing => _isEditing;

    /// <summary>搜索结果视图地址栏文字，非空时单段不可点</summary>
    public static readonly DependencyProperty SearchLocationTextProperty = DependencyProperty.Register(
        nameof(SearchLocationText), typeof(string), typeof(AddressBar),
        new PropertyMetadata(string.Empty, OnLocationChanged));

    /// <summary>搜索结果视图地址栏文字，空串走普通面包屑</summary>
    public string SearchLocationText
    {
        get => (string)GetValue(SearchLocationTextProperty);
        set => SetValue(SearchLocationTextProperty, value);
    }

    private static void OnLocationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((AddressBar)d).RebuildBreadcrumb();

    // 面包屑

    private void RebuildBreadcrumb()
    {
        if (_isEditing)
        {
            EndEdit(clearError: true);
        }

        SegmentPanel.Children.Clear();
        _segments.Clear();
        _segmentElements.Clear();

        // 搜索结果视图只显示一段，不可点击、无下拉
        if (!string.IsNullOrEmpty(SearchLocationText))
        {
            IReadOnlyList<BreadcrumbSegment> searchSegments = new[]
            {
                new BreadcrumbSegment(SearchLocationText, ThisPcToken, IsThisPc: true),
            };

            for (int i = 0; i < searchSegments.Count; i++)
            {
                BreadcrumbSegment segment = searchSegments[i];
                var group = new List<UIElement>();
                var button = new Button
                {
                    Content = segment.DisplayName,
                    Style = (Style)FindResource("CrumbButtonStyle"),
                    Tag = segment,
                    ToolTip = segment.DisplayName,
                };
                AutomationProperties.SetName(button, segment.DisplayName);
                AutomationProperties.SetAutomationId(button, $"Crumb_{i}");
                // 搜索结果段没有真实路径，不能点
                button.IsHitTestVisible = false;
                SegmentPanel.Children.Add(button);
                group.Add(button);
                _segments.Add(segment);
                _segmentElements.Add(group);
            }

            CollapseButton.Visibility = Visibility.Collapsed;
            return;
        }

        IReadOnlyList<BreadcrumbSegment> segments = Breadcrumbs.Build(
            Location, ThisPcName, ThisPcToken, RecycleBinName, RecycleBinToken);

        for (int i = 0; i < segments.Count; i++)
        {
            BreadcrumbSegment segment = segments[i];
            var group = new List<UIElement>();
            bool isRecycleBin = !string.IsNullOrEmpty(RecycleBinToken)
                && string.Equals(segment.Target, RecycleBinToken, StringComparison.Ordinal);

            var button = new Button
            {
                Content = segment.DisplayName,
                Style = (Style)FindResource("CrumbButtonStyle"),
                Tag = segment,
                ToolTip = segment.IsThisPc || isRecycleBin ? segment.DisplayName : segment.Target,
            };
            AutomationProperties.SetName(button, segment.DisplayName);
            AutomationProperties.SetAutomationId(button, $"Crumb_{i}");
            button.Click += OnSegmentClick;
            SegmentPanel.Children.Add(button);
            group.Add(button);

            // 每段后的 › 弹出该层子文件夹，回收站除外
            if (isRecycleBin)
            {
                _segments.Add(segment);
                _segmentElements.Add(group);
                continue;
            }

            var chevron = new Button
            {
                Style = (Style)FindResource("CrumbChevronStyle"),
                Tag = segment,
                ToolTip = "展开子文件夹",
            };
            AutomationProperties.SetName(chevron, $"展开 {segment.DisplayName}");
            AutomationProperties.SetAutomationId(chevron, $"CrumbArrow_{i}");
            chevron.Click += OnChevronClick;
            SegmentPanel.Children.Add(chevron);
            group.Add(chevron);

            _segments.Add(segment);
            _segmentElements.Add(group);
        }

        UpdateCollapseState();
    }

    private void UpdateCollapseState()
    {
        if (_isEditing || _segments.Count == 0)
        {
            return;
        }

        // 先全部显示、隐藏折叠按钮，再测量
        foreach (List<UIElement> group in _segmentElements)
        {
            foreach (UIElement element in group)
            {
                element.Visibility = Visibility.Visible;
            }
        }

        CollapseButton.Visibility = Visibility.Collapsed;

        double available = SegmentScroller.ActualWidth > 0
            ? SegmentScroller.ActualWidth
            : Math.Max(120, ActualWidth - 40);

        double collapseWidth = MeasureWidth(CollapseButton);

        double MeasureFrom(int start)
        {
            double total = 0;
            for (int i = start; i < _segmentElements.Count; i++)
            {
                total += MeasureGroup(_segmentElements[i]);
            }

            return total;
        }

        int collapsedCount = 0;

        if (MeasureFrom(0) > available)
        {
            for (int c = 1; c < _segments.Count; c++)
            {
                double total = MeasureFrom(c) + collapseWidth;
                if (total <= available || c == _segments.Count - 1)
                {
                    collapsedCount = c;
                    break;
                }
            }
        }

        ApplyCollapse(collapsedCount);
    }

    private void ApplyCollapse(int collapsedCount)
    {
        for (int i = 0; i < _segmentElements.Count; i++)
        {
            Visibility visibility = i >= collapsedCount ? Visibility.Visible : Visibility.Collapsed;
            foreach (UIElement element in _segmentElements[i])
            {
                element.Visibility = visibility;
            }
        }

        _collapsedSegments = _segments.Take(collapsedCount).ToArray();
        CollapseButton.Visibility = collapsedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static double MeasureGroup(IEnumerable<UIElement> group)
    {
        double total = 0;
        foreach (UIElement element in group)
        {
            total += MeasureWidth(element);
        }

        return total;
    }

    private static double MeasureWidth(UIElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return element.DesiredSize.Width;
    }

    private void OnSegmentClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: BreadcrumbSegment segment })
        {
            RaiseNavigate(segment.Target);
        }

        e.Handled = true;
    }

    private void OnChevronClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: BreadcrumbSegment segment } anchor)
        {
            ShowSubfolderPopup(segment, anchor);
        }

        e.Handled = true;
    }

    private void OnCollapseClick(object sender, RoutedEventArgs e)
    {
        DropPanel.Children.Clear();
        foreach (BreadcrumbSegment segment in _collapsedSegments)
        {
            DropPanel.Children.Add(CreatePopupItem(
                segment.IsThisPc ? ThisPcName : segment.DisplayName,
                segment.Target,
                segment.IsThisPc ? ThisPcName : segment.Target));
        }

        OpenPopup(CollapseButton);
        e.Handled = true;
    }

    // 子文件夹下拉

    private void ShowSubfolderPopup(BreadcrumbSegment segment, Button anchor)
    {
        DropPanel.Children.Clear();
        DropPanel.Children.Add(CreateInfoText("正在加载…"));

        OpenPopup(anchor);

        int generation = ++_popupGeneration;
        string target = segment.Target;
        bool isThisPc = segment.IsThisPc;

        _ = Task.Run(() =>
        {
            IReadOnlyList<(string Name, string Path)> items = isThisPc ? GetDrives() : GetSubfolders(target);

            Dispatcher.Invoke(() =>
            {
                if (generation != _popupGeneration || !DropPopup.IsOpen)
                {
                    return;
                }

                FillPopupItems(items);
            });
        });
    }

    private static IReadOnlyList<(string Name, string Path)> GetDrives()
    {
        var result = new List<(string, string)>();
        foreach (DriveItem drive in LocationProvider.GetDrives())
        {
            if (drive.IsReady)
            {
                result.Add((drive.DisplayName, drive.RootPath));
            }
        }

        return result;
    }

    private static IReadOnlyList<(string Name, string Path)> GetSubfolders(string path)
    {
        var result = new List<(string, string)>();
        IReadOnlyList<SubfolderItem> folders = SubfolderProvider.GetSubfolders(
            path,
            ExplorerSettings.ShowHiddenFiles,
            ExplorerSettings.ShowSystemFiles);

        foreach (SubfolderItem folder in folders)
        {
            result.Add((folder.Name, folder.DisplayPath));
        }

        return result;
    }

    private void FillPopupItems(IReadOnlyList<(string Name, string Path)> items)
    {
        DropPanel.Children.Clear();

        if (items.Count == 0)
        {
            DropPanel.Children.Add(CreateInfoText("（没有可显示的子文件夹）"));
            return;
        }

        foreach ((string name, string path) in items)
        {
            DropPanel.Children.Add(CreatePopupItem(name, path, path));
        }
    }

    private Button CreatePopupItem(string display, string target, string tooltip)
    {
        var button = new Button
        {
            Content = display,
            Style = (Style)FindResource("PopupItemStyle"),
            ToolTip = tooltip,
            Tag = target,
        };
        AutomationProperties.SetName(button, display);
        button.Click += (_, e) =>
        {
            DropPopup.IsOpen = false;
            RaiseNavigate((string)button.Tag);
            e.Handled = true;
        };

        return button;
    }

    private static TextBlock CreateInfoText(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            Margin = new Thickness(10, 6, 10, 6),
            FontSize = 13,
        };

        // 必须用 DynamicResource，换肤才能即时生效
        block.SetResourceReference(TextBlock.ForegroundProperty, "CjTextSecondaryBrush");

        return block;
    }

    private void OpenPopup(UIElement anchor)
    {
        DropPopup.PlacementTarget = anchor;
        DropPopup.IsOpen = true;
    }

    // 编辑模式

    /// <summary>进入编辑模式</summary>
    public void BeginEdit()
    {
        if (_isEditing)
        {
            EditBox.Focus();
            EditBox.SelectAll();
            return;
        }

        _isEditing = true;
        ClearError();

        EditBox.Text = BuildEditText();
        BreadcrumbHost.Visibility = Visibility.Collapsed;
        CollapseButton.Visibility = Visibility.Collapsed;
        EditBox.Visibility = Visibility.Visible;

        EditBox.Focus();
        EditBox.SelectAll();
    }

    /// <summary>退出编辑模式，恢复面包屑</summary>
    public void CancelEdit()
    {
        EndEdit(clearError: true);
        Focus();
    }

    private void EndEdit(bool clearError)
    {
        if (!_isEditing)
        {
            return;
        }

        _isEditing = false;
        EditBox.Visibility = Visibility.Collapsed;
        BreadcrumbHost.Visibility = Visibility.Visible;
        if (clearError)
        {
            ClearError();
        }

        if (EditBox.IsKeyboardFocusWithin)
        {
            // 隐藏编辑框后焦点会悬空，转给控件本身
            Focus();
        }

        UpdateCollapseState();
    }

    /// <summary>编辑模式显示的完整普通路径</summary>
    private string BuildEditText()
    {
        string location = Location ?? string.Empty;

        if (string.IsNullOrEmpty(location)
            || string.Equals(location, ThisPcToken, StringComparison.Ordinal)
            || string.Equals(location, ThisPcName, StringComparison.OrdinalIgnoreCase))
        {
            return ThisPcName;
        }

        if (string.Equals(location, RecycleBinToken, StringComparison.Ordinal)
            || string.Equals(location, RecycleBinName, StringComparison.OrdinalIgnoreCase))
        {
            return RecycleBinName;
        }

        return PathHelper.ToDisplay(location);
    }

    private void OnChromeMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_isEditing)
        {
            return;
        }

        // 只有点到空白才进入编辑，点段按钮由按钮自己处理
        if (e.OriginalSource is FrameworkElement { } source && IsInteractive(source))
        {
            return;
        }

        BeginEdit();
    }

    private static bool IsInteractive(DependencyObject source)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is ButtonBase)
            {
                return true;
            }

            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void OnEditBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitEdit();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            EndEdit(clearError: true);
            e.Handled = true;
        }
    }

    private void OnEditBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_isEditing)
        {
            EndEdit(clearError: true);
        }
    }

    private void CommitEdit()
    {
        string text = (EditBox.Text ?? string.Empty).Trim();

        string target;
        if (text.Length == 0)
        {
            ShowError("请输入路径。");
            return;
        }

        if (string.Equals(text, ThisPcName, StringComparison.OrdinalIgnoreCase))
        {
            target = ThisPcToken;
        }
        else if (string.Equals(text, RecycleBinName, StringComparison.OrdinalIgnoreCase))
        {
            target = RecycleBinToken;
        }
        else if (PathHelper.TryParseUserInput(text, LocationProvider.ResolveAlias, out PathParseResult result))
        {
            target = result.DisplayPath;
        }
        else
        {
            // 路径无效时下方显示原因，留在编辑模式
            ShowError(result.Error);
            return;
        }

        ClearError();
        EndEdit(clearError: true);
        RaiseNavigate(target);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void ClearError()
    {
        ErrorText.Text = string.Empty;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void RaiseNavigate(string target)
    {
        if (string.IsNullOrEmpty(target))
        {
            return;
        }

        NavigateRequested?.Invoke(this, new AddressBarNavigateEventArgs(target));
    }
}
