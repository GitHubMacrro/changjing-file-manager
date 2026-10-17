using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ChangJing.App.Controls;

/// <summary>单行中间省略文本控件，省略中间、保留结尾扩展名</summary>
// TextBlock 的 TextTrimming 只从末尾省略会砍掉扩展名，故按可用宽度动态重建文本
public sealed class MiddleEllipsisTextBlock : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(MiddleEllipsisTextBlock),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground),
        typeof(Brush),
        typeof(MiddleEllipsisTextBlock),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyProperty = DependencyProperty.Register(
        nameof(FontFamily),
        typeof(FontFamily),
        typeof(MiddleEllipsisTextBlock),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize),
        typeof(double),
        typeof(MiddleEllipsisTextBlock),
        new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontWeightProperty = DependencyProperty.Register(
        nameof(FontWeight),
        typeof(FontWeight),
        typeof(MiddleEllipsisTextBlock),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontWeight, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EllipsisProperty = DependencyProperty.Register(
        nameof(Ellipsis),
        typeof(string),
        typeof(MiddleEllipsisTextBlock),
        new FrameworkPropertyMetadata("…", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>是否保留最后一段路径，文件夹路径列用</summary>
    public static readonly DependencyProperty KeepLastSegmentProperty = DependencyProperty.Register(
        nameof(KeepLastSegment),
        typeof(bool),
        typeof(MiddleEllipsisTextBlock),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>搜索结果关键词高亮，匹配片段加粗</summary>
    public static readonly DependencyProperty HighlightKeywordProperty = DependencyProperty.Register(
        nameof(HighlightKeyword),
        typeof(string),
        typeof(MiddleEllipsisTextBlock),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HighlightBackgroundProperty = DependencyProperty.Register(
        nameof(HighlightBackground),
        typeof(Brush),
        typeof(MiddleEllipsisTextBlock),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private string _rendered = string.Empty;

    public string RenderedText => _rendered;

    public bool IsTruncated => !string.Equals(_rendered, Text ?? string.Empty, StringComparison.Ordinal);

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
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

    public bool KeepLastSegment
    {
        get => (bool)GetValue(KeepLastSegmentProperty);
        set => SetValue(KeepLastSegmentProperty, value);
    }

    public string HighlightKeyword
    {
        get => (string)GetValue(HighlightKeywordProperty);
        set => SetValue(HighlightKeywordProperty, value);
    }

    public Brush? HighlightBackground
    {
        get => (Brush?)GetValue(HighlightBackgroundProperty);
        set => SetValue(HighlightBackgroundProperty, value);
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (!TryGetTypeface(out Typeface typeface, out double pixelsPerDip))
        {
            return new Size(0, FontSize + 4);
        }

        FormattedText full = CreateFormattedText(Text ?? string.Empty, typeface, pixelsPerDip);
        double height = full.Height;
        double desiredWidth = full.WidthIncludingTrailingWhitespace;

        if (double.IsInfinity(availableSize.Width))
        {
            _rendered = Text ?? string.Empty;
            return new Size(desiredWidth, height);
        }

        double maxWidth = Math.Max(0, availableSize.Width);
        _rendered = BuildEllipsized(Text ?? string.Empty, maxWidth, typeface, pixelsPerDip);

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

        // 高亮在实际渲染的文本上算，省略号不参与匹配
        string keyword = HighlightKeyword ?? string.Empty;
        if (keyword.Length == 0)
        {
            drawingContext.DrawText(text, new Point(0, y));
            return;
        }

        IReadOnlyList<Services.Search.HighlightSegment> segments = Services.Search.SearchHighlight.Compute(_rendered, keyword);
        bool hasMatch = false;
        foreach (Services.Search.HighlightSegment segment in segments)
        {
            if (segment.IsMatch)
            {
                hasMatch = true;
                break;
            }
        }

        if (!hasMatch)
        {
            drawingContext.DrawText(text, new Point(0, y));
            return;
        }

        Brush? background = HighlightBackground;
        double x = 0;
        foreach (Services.Search.HighlightSegment segment in segments)
        {
            string part = _rendered.Substring(segment.Start, segment.Length);
            FormattedText run = segment.IsMatch
                ? CreateFormattedText(part, typeface, pixelsPerDip, FontWeights.Bold)
                : CreateFormattedText(part, typeface, pixelsPerDip);

            double width = run.WidthIncludingTrailingWhitespace;
            if (segment.IsMatch && background is not null && width > 0)
            {
                drawingContext.DrawRectangle(background, null, new Rect(x, y, width, text.Height));
            }

            drawingContext.DrawText(run, new Point(x, y));
            x += width;
        }
    }

    private string BuildEllipsized(string full, double maxWidth, Typeface typeface, double pixelsPerDip)
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

        if (KeepLastSegment)
        {
            return BuildPathEllipsized(full, maxWidth, ellipsis, typeface, pixelsPerDip);
        }

        string extension = ExtractExtension(full);
        string stem = full.Substring(0, full.Length - extension.Length);

        // 先试「开头 + … + 扩展名」
        double tailWidth = Measure(ellipsis + extension, typeface, pixelsPerDip);
        if (tailWidth < maxWidth && extension.Length > 0)
        {
            int headLength = LargestPrefixLength(stem, maxWidth - tailWidth, typeface, pixelsPerDip);
            if (headLength > 0)
            {
                return stem.Substring(0, headLength) + ellipsis + extension;
            }
        }

        // 扩展名放不下或无扩展名时退化为「开头 + …」
        int fallbackLength = LargestPrefixLength(full, maxWidth - Measure(ellipsis, typeface, pixelsPerDip), typeface, pixelsPerDip);
        if (fallbackLength <= 0)
        {
            return ellipsis;
        }

        return full.Substring(0, fallbackLength) + ellipsis;
    }

    /// <summary>路径专用中间省略，保留根 + … + 末尾若干段</summary>
    private string BuildPathEllipsized(string full, double maxWidth, string ellipsis, Typeface typeface, double pixelsPerDip)
    {
        // 按分隔符切分并保留分隔符，方便原样拼回
        var segments = new List<string>();
        int start = 0;
        for (int i = 0; i < full.Length; i++)
        {
            if (full[i] is '\\' or '/')
            {
                if (i + 1 > start)
                {
                    segments.Add(full.Substring(start, i + 1 - start));
                    start = i + 1;
                }
            }
        }

        if (start < full.Length)
        {
            segments.Add(full.Substring(start));
        }

        if (segments.Count <= 2)
        {
            // 段太少时退化为「开头 + …」
            int headLength = LargestPrefixLength(full, maxWidth - Measure(ellipsis, typeface, pixelsPerDip), typeface, pixelsPerDip);
            return headLength <= 0 ? ellipsis : full.Substring(0, headLength) + ellipsis;
        }

        // 头部取根，如 C:\ 或 \\server\share\
        string head = segments[0];
        if (head.StartsWith(@"\\", StringComparison.Ordinal) && segments.Count >= 3)
        {
            head += segments[1];
        }

        // 尾部取尽量多的末尾段，中间用省略号
        string tail = segments[^1];
        int tailStart = segments.Count - 1;
        for (int i = segments.Count - 2; i >= 1; i--)
        {
            string candidate = string.Concat(segments.Skip(i));
            if (Measure(head + ellipsis + candidate, typeface, pixelsPerDip) <= maxWidth)
            {
                tail = candidate;
                tailStart = i;
            }
            else
            {
                break;
            }
        }

        // 头部放不下时只保留根 + … + 尾
        if (Measure(head + ellipsis + tail, typeface, pixelsPerDip) > maxWidth)
        {
            // 尾部也放不下时从头裁尾
            int budget = (int)Math.Max(0, maxWidth - Measure(head + ellipsis, typeface, pixelsPerDip));
            int tailLength = LargestPrefixLength(tail, budget, typeface, pixelsPerDip);
            tail = tailLength <= 0 ? tail[^1].ToString() : tail.Substring(0, tailLength);
        }

        _ = tailStart;
        return head + ellipsis + tail;
    }

    /// <summary>二分求最长的不超过 budget 的前缀字符数</summary>
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

    /// <summary>取扩展名含点，无扩展名返回空串</summary>
    private static string ExtractExtension(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? string.Empty : name.Substring(dot);
    }

    private double Measure(string text, Typeface typeface, double pixelsPerDip)
        => string.IsNullOrEmpty(text) ? 0 : CreateFormattedText(text, typeface, pixelsPerDip).WidthIncludingTrailingWhitespace;

    private FormattedText CreateFormattedText(string text, Typeface typeface, double pixelsPerDip)
    {
        // 别给 MaxTextWidth 赋 PositiveInfinity，FormattedText 会抛异常
        return new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            FontSize,
            Foreground,
            pixelsPerDip);
    }

    private FormattedText CreateFormattedText(string text, Typeface typeface, double pixelsPerDip, FontWeight weight)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(typeface.FontFamily, typeface.Style, weight, typeface.Stretch),
            FontSize,
            Foreground,
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
        }

        return typeface is not null;
    }
}
