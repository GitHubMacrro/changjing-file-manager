using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ChangJing.App.Controls;

/// <summary>标题栏文字：主文字常规色 + 后缀次要灰色，空间不足时保留开头的「畅径」、对位置名做末尾省略</summary>
public sealed class TitleBarText : FrameworkElement
{
    public static readonly DependencyProperty MainTextProperty = DependencyProperty.Register(
        nameof(MainText),
        typeof(string),
        typeof(TitleBarText),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SuffixTextProperty = DependencyProperty.Register(
        nameof(SuffixText),
        typeof(string),
        typeof(TitleBarText),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MainForegroundProperty = DependencyProperty.Register(
        nameof(MainForeground),
        typeof(Brush),
        typeof(TitleBarText),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>后缀文字前景色，为空时退回主色</summary>
    public static readonly DependencyProperty SuffixForegroundProperty = DependencyProperty.Register(
        nameof(SuffixForeground),
        typeof(Brush),
        typeof(TitleBarText),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyProperty = DependencyProperty.Register(
        nameof(FontFamily),
        typeof(FontFamily),
        typeof(TitleBarText),
        new FrameworkPropertyMetadata(
            SystemFonts.MessageFontFamily,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize),
        typeof(double),
        typeof(TitleBarText),
        new FrameworkPropertyMetadata(
            SystemFonts.MessageFontSize,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontWeightProperty = DependencyProperty.Register(
        nameof(FontWeight),
        typeof(FontWeight),
        typeof(TitleBarText),
        new FrameworkPropertyMetadata(
            SystemFonts.MessageFontWeight,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EllipsisProperty = DependencyProperty.Register(
        nameof(Ellipsis),
        typeof(string),
        typeof(TitleBarText),
        new FrameworkPropertyMetadata("…", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    private string _rendered = string.Empty;

    public string FullText => (MainText ?? string.Empty) + (SuffixText ?? string.Empty);

    /// <summary>当前实际渲染的文字，含末尾省略号</summary>
    public string RenderedText => _rendered;

    public bool IsTruncated => !string.Equals(_rendered, FullText, StringComparison.Ordinal);

    public string MainText
    {
        get => (string)GetValue(MainTextProperty);
        set => SetValue(MainTextProperty, value);
    }

    public string SuffixText
    {
        get => (string)GetValue(SuffixTextProperty);
        set => SetValue(SuffixTextProperty, value);
    }

    public Brush MainForeground
    {
        get => (Brush)GetValue(MainForegroundProperty);
        set => SetValue(MainForegroundProperty, value);
    }

    public Brush? SuffixForeground
    {
        get => (Brush?)GetValue(SuffixForegroundProperty);
        set => SetValue(SuffixForegroundProperty, value);
    }

    public FontFamily FontFamily
    {
        get => (FontFamily)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontWeight FontWeight
    {
        get => (FontWeight)GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public string Ellipsis
    {
        get => (string)GetValue(EllipsisProperty);
        set => SetValue(EllipsisProperty, value);
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (!TryGetTypeface(out Typeface typeface, out double pixelsPerDip))
        {
            return new Size(0, FontSize + 4);
        }

        string full = FullText;
        FormattedText formatted = CreateFormattedText(full, typeface, pixelsPerDip);
        double height = formatted.Height;
        double desiredWidth = formatted.WidthIncludingTrailingWhitespace;

        if (double.IsInfinity(availableSize.Width))
        {
            _rendered = full;
            return new Size(desiredWidth, height);
        }

        double maxWidth = Math.Max(0, availableSize.Width);
        _rendered = BuildTailEllipsized(full, maxWidth, typeface, pixelsPerDip);

        return new Size(Math.Min(desiredWidth, maxWidth), height);
    }

    /// <inheritdoc/>
    protected override void OnRender(DrawingContext drawingContext)
    {
        if (string.IsNullOrEmpty(_rendered) || !TryGetTypeface(out Typeface typeface, out double pixelsPerDip))
        {
            return;
        }

        FormattedText text = CreateFormattedText(_rendered, typeface, pixelsPerDip);
        double y = (RenderSize.Height - text.Height) / 2;
        double x = 0;
        int boundary = (MainText ?? string.Empty).Length;
        string ellipsis = string.IsNullOrEmpty(Ellipsis) ? "…" : Ellipsis;

        if (!IsTruncated)
        {
            DrawRange(drawingContext, _rendered, 0, _rendered.Length, fullStart: 0, boundary, y, typeface, pixelsPerDip, ref x);
            return;
        }

        // 已省略：形如「畅径 - 位置名…」
        int prefixLength = Math.Max(0, _rendered.Length - ellipsis.Length);

        DrawRange(drawingContext, _rendered, 0, prefixLength, fullStart: 0, boundary, y, typeface, pixelsPerDip, ref x);

        DrawText(drawingContext, ellipsis, SuffixForeground ?? MainForeground, y, typeface, pixelsPerDip, ref x);
    }

    private void DrawRange(
        DrawingContext drawingContext,
        string source,
        int sourceStart,
        int sourceEnd,
        int fullStart,
        int boundary,
        double y,
        Typeface typeface,
        double pixelsPerDip,
        ref double x)
    {
        if (sourceEnd <= sourceStart)
        {
            return;
        }

        string segment = source.Substring(sourceStart, sourceEnd - sourceStart);
        int localBoundary = boundary - fullStart;

        if (localBoundary <= 0)
        {
            DrawText(drawingContext, segment, SuffixForeground ?? MainForeground, y, typeface, pixelsPerDip, ref x);
            return;
        }

        if (localBoundary >= segment.Length)
        {
            DrawText(drawingContext, segment, MainForeground, y, typeface, pixelsPerDip, ref x);
            return;
        }

        DrawText(drawingContext, segment.Substring(0, localBoundary), MainForeground, y, typeface, pixelsPerDip, ref x);
        DrawText(
            drawingContext,
            segment.Substring(localBoundary),
            SuffixForeground ?? MainForeground,
            y,
            typeface,
            pixelsPerDip,
            ref x);
    }

    private void DrawText(
        DrawingContext drawingContext,
        string text,
        Brush brush,
        double y,
        Typeface typeface,
        double pixelsPerDip,
        ref double x)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        FormattedText formatted = CreateFormattedText(text, typeface, pixelsPerDip, brush);
        drawingContext.DrawText(formatted, new Point(x, y));
        x += formatted.WidthIncludingTrailingWhitespace;
    }

    // 保留开头：优先完整保住应用名标签，剩余宽度给位置名，放不下就截断补省略号
    private string BuildTailEllipsized(string full, double maxWidth, Typeface typeface, double pixelsPerDip)
    {
        if (full.Length == 0)
        {
            return full;
        }

        if (Measure(full, typeface, pixelsPerDip) <= maxWidth)
        {
            return full;
        }

        string ellipsis = string.IsNullOrEmpty(Ellipsis) ? "…" : Ellipsis;
        double ellipsisWidth = Measure(ellipsis, typeface, pixelsPerDip);
        double budget = Math.Max(0, maxWidth - ellipsisWidth);

        if (budget <= 0)
        {
            return ellipsis;
        }

        string main = MainText ?? string.Empty;
        string suffix = SuffixText ?? string.Empty;

        int headLength = LargestPrefixLength(main, budget, typeface, pixelsPerDip);

        if (headLength < main.Length)
        {
            // 应用名本身都放不下：退化为「前缀…」
            return headLength <= 0 ? ellipsis : main.Substring(0, headLength) + ellipsis;
        }

        // 应用名完整保留，剩余宽度给位置名后缀
        double tailBudget = Math.Max(0, budget - Measure(main, typeface, pixelsPerDip));
        string tail = LargestPrefix(suffix, tailBudget, typeface, pixelsPerDip);
        return main + tail + ellipsis;
    }

    private int LargestPrefixLength(string text, double budget, Typeface typeface, double pixelsPerDip)
    {
        if (budget <= 0 || text.Length == 0)
        {
            return 0;
        }

        int low = 0;
        int high = text.Length;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (Measure(text.Substring(0, mid), typeface, pixelsPerDip) <= budget)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low;
    }

    private string LargestPrefix(string text, double budget, Typeface typeface, double pixelsPerDip)
        => LargestPrefixLength(text, budget, typeface, pixelsPerDip) is int length && length > 0
            ? text.Substring(0, length)
            : string.Empty;

    private double Measure(string text, Typeface typeface, double pixelsPerDip)
        => string.IsNullOrEmpty(text)
            ? 0
            : CreateFormattedText(text, typeface, pixelsPerDip).WidthIncludingTrailingWhitespace;

    private FormattedText CreateFormattedText(string text, Typeface typeface, double pixelsPerDip)
        => CreateFormattedText(text, typeface, pixelsPerDip, MainForeground);

    private FormattedText CreateFormattedText(string text, Typeface typeface, double pixelsPerDip, Brush brush)
    {
        // MaxTextWidth 不能赋 PositiveInfinity，FormattedText 会抛异常
        return new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            FontSize,
            brush,
            pixelsPerDip);
    }

    private bool TryGetTypeface(out Typeface typeface, out double pixelsPerDip)
    {
        typeface = new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal);
        pixelsPerDip = 1.0;

        try
        {
            pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            if (pixelsPerDip <= 0)
            {
                pixelsPerDip = 1.0;
            }
        }
        catch
        {
            // 不在可视化树中 GetDpi 会抛异常，用默认值
        }

        return typeface is not null;
    }
}
