using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

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
        public SelectionBandAdorner? Adorner;
        public AdornerLayer? Layer;
        public bool Additive;
        public readonly List<object> Baseline = new();
    }

    private static readonly Dictionary<ListView, DragState> States = new();

    private static DragState GetState(ListView listView)
    {
        if (!States.TryGetValue(listView, out DragState? state))
        {
            state = new DragState();
            States[listView] = state;
            listView.Unloaded += (_, _) => States.Remove(listView);
        }

        return state;
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListView listView)
        {
            return;
        }

        // 点在行／列头／滚动条上时不介入，NameCellOnly 下点行内空白按背景处理
        var hitItem = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
        bool nameCellOnly = GetNameCellOnly(listView);
        bool onNameContent = ListHitTest.IsNameContentHit(e.OriginalSource as DependencyObject);

        if (hitItem is not null && (!nameCellOnly || onNameContent))
        {
            return;
        }

        if (FindAncestor<GridViewColumnHeader>(e.OriginalSource as DependencyObject) is not null
            || FindAncestor<ScrollBar>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        DragState state = GetState(listView);
        state.Active = true;
        state.Start = e.GetPosition(listView);
        state.Additive = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

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

        Point current = e.GetPosition(listView);
        Rect band = new(state.Start, current);

        // 拖动距离太小时不显示框，避免单击空白闪烁
        if (band.Width < 3 && band.Height < 3)
        {
            return;
        }

        EnsureAdorner(listView, state);
        state.Adorner!.Band = band;
        state.Adorner.InvalidateVisual();

        SelectInBand(listView, band, state.Additive, state.Baseline);
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

        if (state.Adorner is not null && state.Layer is not null)
        {
            state.Layer.Remove(state.Adorner);
        }

        state.Adorner = null;
        state.Layer = null;
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

        public SelectionBandAdorner(UIElement adornedElement)
            : base(adornedElement)
        {
            _pen = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0x00, 0x78, 0xD4)), 1);
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

            var fill = new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x78, 0xD4));
            fill.Freeze();
            drawingContext.DrawRectangle(fill, _pen, Band);
        }
    }
}
