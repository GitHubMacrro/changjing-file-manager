using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ChangJing.App.Controls;

/// <summary>拖放悬停目标的高亮框</summary>
// 用 Adorner 而不是改元素背景，虚拟化列表项的背景由 WPF-UI 控制，改 Background 可能不生效
public sealed class DropHighlightAdorner : Adorner
{
    private readonly Pen _borderPen;
    private readonly Brush _fill;

    /// <summary>创建高亮框</summary>
    public DropHighlightAdorner(UIElement adornedElement, Color accent, Color fill)
        : base(adornedElement)
    {
        _borderPen = new Pen(new SolidColorBrush(accent), 2);
        _borderPen.Brush.Freeze();
        _fill = new SolidColorBrush(fill);
        _fill.Freeze();

        IsHitTestVisible = false;
    }

    /// <inheritdoc/>
    protected override void OnRender(DrawingContext drawingContext)
    {
        var rect = new Rect(AdornedElement.RenderSize);
        drawingContext.DrawRectangle(_fill, _borderPen, rect);
    }
}
