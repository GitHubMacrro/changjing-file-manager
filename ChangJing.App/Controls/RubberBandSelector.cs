using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ChangJing.App.Controls;

/// <summary>给 ListView 增加空白处拖框多选（资源管理器风格）</summary>
public static class RubberBandSelector
{
    /// <summary>是否启用框选</summary>
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(RubberBandSelector),
            new PropertyMetadata(false, OnIsEnabledChanged));

    /// <summary>读取是否启用框选</summary>
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    /// <summary>设置是否启用框选</summary>
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    /// <summary>为 true 时只有点在名称内容上才选中行，行内空白按背景处理</summary>
    public static readonly DependencyProperty NameCellOnlyProperty =
        DependencyProperty.RegisterAttached(
            "NameCellOnly",
            typeof(bool),
            typeof(RubberBandSelector),
            new PropertyMetadata(false));

    /// <summary>读取是否只在名称内容上选中行</summary>
    public static bool GetNameCellOnly(DependencyObject element) => (bool)element.GetValue(NameCellOnlyProperty);

    /// <summary>设置是否只在名称内容上选中行</summary>
    public static void SetNameCellOnly(DependencyObject element, bool value) => element.SetValue(NameCellOnlyProperty, value);

    /// <summary>按选择框选中与框相交的已实现行</summary>
    public static void SelectInBand(
        ListView listView,
        Rect band,
        bool additive,
        IReadOnlyList<object>? baseline = null)
    {
        ArgumentNullException.ThrowIfNull(listView);

        var selected = new HashSet<object>();

        if (additive)
        {
            IReadOnlyList<object> keep = baseline ?? listView.SelectedItems.Cast<object>().ToList();
            foreach (object item in keep)
            {
                selected.Add(item);
            }
        }

        // 虚拟化列表只对已实现的行做命中判断
        int count = listView.Items.Count;
        for (int index = 0; index < count; index++)
        {
            if (listView.ItemContainerGenerator.ContainerFromIndex(index) is not ListViewItem container
                || container.DataContext is not { } dataContext)
            {
                continue;
            }

            if (band.IntersectsWith(GetBounds(container, listView)))
            {
                selected.Add(dataContext);
            }
        }

        var current = new HashSet<object>();
        foreach (object item in listView.SelectedItems)
        {
            current.Add(item);
        }

        foreach (object item in selected)
        {
            if (!current.Contains(item))
            {
                listView.SelectedItems.Add(item);
            }
        }

        foreach (object item in current)
        {
            if (!selected.Contains(item))
            {
                listView.SelectedItems.Remove(item);
            }
        }
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListView listView)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            listView.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
            listView.PreviewMouseMove += OnPreviewMouseMove;
            listView.PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
            listView.LostMouseCapture += OnLostMouseCapture;
        }
        else
        {
            listView.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
            listView.PreviewMouseMove -= OnPreviewMouseMove;
            listView.PreviewMouseLeftButtonUp -= OnPreviewMouseLeftButtonUp;
            listView.LostMouseCapture -= OnLostMouseCapture;
        }
    }

    // 每个 ListView 一份拖动状态
    private sealed class DragState
    {
        public bool Active;
        public Point Start;
        public Point Current;
        public SelectionBandAdorner? Adorner;
        public AdornerLayer? Layer;
        public bool Additive;
        public ScrollViewer? Scroll;
        public DispatcherTimer? ScrollTimer;
        public readonly List<object> Baseline = new();
    }

    private static readonly Dictionary<ListView, DragState> States = new();

    // 边缘多少像素内开始自动滚动
    private const double AutoScrollMargin = 24;

    private static DragState GetState(ListView listView)
    {
        if (!States.TryGetValue(listView, out DragState? state))
        {
            state = new DragState();
            States[listView] = state;
            listView.Unloaded += (_, _) =>
            {
                StopAutoScroll(state);
                States.Remove(listView);
            };
        }

        return state;
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListView listView)
        {
            return;
        }

        DependencyObject? source = e.OriginalSource as DependencyObject;

        // 行内交互控件（复选框）自己处理点击，不介入
        if (ListHitTest.IsItemChromeHit(source))
        {
            return;
        }

        // 点在行／列头／滚动条上时不介入，NameCellOnly 下点行内空白按背景处理
        var hitItem = FindAncestor<ListViewItem>(source);
        bool nameCellOnly = GetNameCellOnly(listView);
        bool onNameContent = ListHitTest.IsNameContentHit(source);

        if (hitItem is not null && (!nameCellOnly || onNameContent))
        {
            return;
        }

        if (FindAncestor<GridViewColumnHeader>(source) is not null
            || FindAncestor<ScrollBar>(source) is not null)
        {
            return;
        }

        DragState state = GetState(listView);
        state.Active = true;
        state.Start = e.GetPosition(listView);
        state.Current = state.Start;
        state.Additive = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        state.Scroll = FindScrollViewer(listView);

        // NameCellOnly 下点行内空白，抑制 ListViewItem 默认选中
        if (nameCellOnly && hitItem is not null)
        {
            e.Handled = true;
        }

        state.Baseline.Clear();
        if (state.Additive)
        {
            foreach (object item in listView.SelectedItems)
            {
                state.Baseline.Add(item);
            }
        }

        // 非追加时先清空，与资源管理器一致
        if (!state.Additive)
        {
            listView.SelectedItems.Clear();
        }

        listView.CaptureMouse();
    }

    private static void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not ListView listView)
        {
            return;
        }

        DragState state = GetState(listView);
        if (!state.Active || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        state.Current = e.GetPosition(listView);
        UpdateBand(listView, state);
        UpdateAutoScroll(listView, state);
    }

    private static void UpdateBand(ListView listView, DragState state)
    {
        Rect band = new(state.Start, state.Current);

        // 拖动距离太小时不显示框，避免单击空白闪烁
        if (band.Width < 3 && band.Height < 3)
        {
            return;
        }

        EnsureAdorner(listView, state);
        if (state.Adorner is not null)
        {
            state.Adorner.Band = band;
            state.Adorner.InvalidateVisual();
        }

        SelectInBand(listView, band, state.Additive, state.Baseline);
    }

    // 拖到列表上下边缘时持续滚动，滚动后按当前指针位置重新判定
    private static void UpdateAutoScroll(ListView listView, DragState state)
    {
        if (state.Scroll is not { } scroll)
        {
            return;
        }

        double y = state.Current.Y;
        double height = listView.ActualHeight;
        double delta = 0;

        if (y < AutoScrollMargin)
        {
            delta = -Math.Max(6, (AutoScrollMargin - y) / 2);
        }
        else if (y > height - AutoScrollMargin)
        {
            delta = Math.Max(6, (y - (height - AutoScrollMargin)) / 2);
        }

        if (delta == 0)
        {
            StopAutoScroll(state);
            return;
        }

        state.ScrollTimer ??= CreateAutoScrollTimer(listView, state);
        state.ScrollTimer.Tag = delta;
        if (!state.ScrollTimer.IsEnabled)
        {
            state.ScrollTimer.Start();
        }
    }

    private static DispatcherTimer CreateAutoScrollTimer(ListView listView, DragState state)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, listView.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(40),
        };

        timer.Tick += (_, _) =>
        {
            if (!state.Active || state.Scroll is not { } scroll)
            {
                StopAutoScroll(state);
                return;
            }

            double delta = timer.Tag is double value ? value : 0;
            double before = scroll.VerticalOffset;
            scroll.ScrollToVerticalOffset(before + delta);

            // 已到顶 / 到底就不再空转
            if (Math.Abs(scroll.VerticalOffset - before) < 0.5)
            {
                StopAutoScroll(state);
                return;
            }

            UpdateBand(listView, state);
        };

        return timer;
    }

    private static void StopAutoScroll(DragState state)
    {
        if (state.ScrollTimer is not null)
        {
            state.ScrollTimer.Stop();
            state.ScrollTimer = null;
        }
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

    private static void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListView listView)
        {
            return;
        }

        Finish(listView);
    }

    private static void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (sender is ListView listView)
        {
            Finish(listView);
        }
    }

    private static void EnsureAdorner(ListView listView, DragState state)
    {
        if (state.Adorner is not null)
        {
            return;
        }

        state.Layer = AdornerLayer.GetAdornerLayer(listView);
        if (state.Layer is null)
        {
            return;
        }

        state.Adorner = new SelectionBandAdorner(listView);
        state.Layer.Add(state.Adorner);
    }

    private static Rect GetBounds(FrameworkElement element, Visual relativeTo)
    {
        try
        {
            GeneralTransform transform = element.TransformToAncestor(relativeTo);
            return transform.TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        catch
        {
            return Rect.Empty;
        }
    }

    private static void Finish(ListView listView)
    {
        DragState state = GetState(listView);
        state.Active = false;
        StopAutoScroll(state);

        if (state.Adorner is not null && state.Layer is not null)
        {
            state.Layer.Remove(state.Adorner);
        }

        state.Adorner = null;
        state.Layer = null;
        state.Scroll = null;
        state.Baseline.Clear();

        if (listView.IsMouseCaptured)
        {
            listView.ReleaseMouseCapture();
        }
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

    // 画选择框的装饰层
    private sealed class SelectionBandAdorner : Adorner
    {
        private readonly Pen _pen;
        private readonly SolidColorBrush _fill;

        public SelectionBandAdorner(UIElement adornedElement)
            : base(adornedElement)
        {
            // 跟随主题强调色，深浅色下都看得清
            Color accent = ResolveAccentColor(adornedElement);
            _fill = new SolidColorBrush(Color.FromArgb(0x33, accent.R, accent.G, accent.B));
            _fill.Freeze();

            _pen = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, accent.R, accent.G, accent.B)), 1);
            _pen.Freeze();

            Band = Rect.Empty;
            IsHitTestVisible = false;
        }

        // 当前选择框，相对被装饰元素
        public Rect Band { get; set; }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            if (Band.IsEmpty || Band.Width <= 0 || Band.Height <= 0)
            {
                return;
            }

            drawingContext.DrawRectangle(_fill, _pen, Band);
        }

        private static Color ResolveAccentColor(UIElement element)
        {
            if (element is FrameworkElement fe && fe.TryFindResource("CjAccentColor") is Color color)
            {
                return color;
            }

            return Color.FromRgb(0x25, 0x63, 0xEB);
        }
    }
}
