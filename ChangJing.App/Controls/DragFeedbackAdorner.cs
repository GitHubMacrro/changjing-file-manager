using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ChangJing.App.Controls;

/// <summary>拖动时随光标显示的文字提示</summary>
// 用 Adorner 而不是 ToolTip，拖动期间鼠标被捕获，Adorner 由窗口层直接绘制、不受捕获影响
public sealed class DragFeedbackAdorner : Adorner
{
    private static readonly Typeface Face = new("Microsoft YaHei UI");

    /// <summary>创建拖动提示</summary>
    public DragFeedbackAdorner(UIElement adornedElement)
        : base(adornedElement)
    {
        IsHitTestVisible = false;
    }

    /// <summary>提示文字</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>光标位置，相对宿主</summary>
    public Point Position { get; set; }

    /// <inheritdoc/>
    protected override void OnRender(DrawingContext drawingContext)
    {
        if (string.IsNullOrEmpty(Text))
        {
            return;
        }

        var text = new FormattedText(
            Text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            Face,
            12,
            Brushes.White,
            1.0);

        const double padX = 8;
        const double padY = 4;
        var size = new Size(text.Width + padX * 2, text.Height + padY * 2);
        var origin = new Point(Position.X + 14, Position.Y + 14);

        var box = new Rect(origin, size);
        var background = new SolidColorBrush(Color.FromArgb(0xE0, 0x1B, 0x1F, 0x24));
        background.Freeze();
        var border = new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), 1);
        border.Brush.Freeze();

        drawingContext.DrawRoundedRectangle(background, border, box, 4, 4);
        drawingContext.DrawText(text, new Point(origin.X + padX, origin.Y + padY));
    }
}
