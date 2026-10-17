using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChangJing.App.Core.LongPath;
using ChangJing.App.Services;

namespace ChangJing.App.Controls;

/// <summary>图标视图里的图片元素：中等及以上尺寸的图片文件异步加载 Shell 缩略图，其它显示系统大图标</summary>
public sealed class ThumbnailImage : Image
{
    /// <summary>文件普通路径</summary>
    public static readonly DependencyProperty FilePathProperty = DependencyProperty.Register(
        nameof(FilePath),
        typeof(string),
        typeof(ThumbnailImage),
        new PropertyMetadata(string.Empty, OnSourcePropertyChanged));

    /// <summary>文件扩展路径</summary>
    public static readonly DependencyProperty LongPathProperty = DependencyProperty.Register(
        nameof(LongPath),
        typeof(string),
        typeof(ThumbnailImage),
        new PropertyMetadata(string.Empty, OnSourcePropertyChanged));

    /// <summary>当前视图的图标边长，像素</summary>
    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize),
        typeof(double),
        typeof(ThumbnailImage),
        new PropertyMetadata(48d, OnSourcePropertyChanged));

    /// <summary>非图片 / 缩略图不可用时的回退图标</summary>
    public static readonly DependencyProperty FallbackIconProperty = DependencyProperty.Register(
        nameof(FallbackIcon),
        typeof(ImageSource),
        typeof(ThumbnailImage),
        new PropertyMetadata(null, OnFallbackIconChanged));

    /// <summary>按尺寸取回退图标的数据源，非 null 时优先于 FallbackIcon / FallbackIconMedium</summary>
    public static readonly DependencyProperty FallbackIconSourceProperty = DependencyProperty.Register(
        nameof(FallbackIconSource),
        typeof(ISizeIconSource),
        typeof(ThumbnailImage),
        new PropertyMetadata(null, OnFallbackIconChanged));

    /// <summary>小尺寸 &lt;= 64 时优先使用的回退图标</summary>
    public static readonly DependencyProperty FallbackIconMediumProperty = DependencyProperty.Register(
        nameof(FallbackIconMedium),
        typeof(ImageSource),
        typeof(ThumbnailImage),
        new PropertyMetadata(null, OnFallbackIconChanged));

    public static readonly DependencyProperty UseThumbnailProperty = DependencyProperty.Register(
        nameof(UseThumbnail),
        typeof(bool),
        typeof(ThumbnailImage),
        new PropertyMetadata(false, OnSourcePropertyChanged));

    private CancellationTokenSource? _cts;
    private string _currentKey = string.Empty;

    /// <summary>文件普通路径</summary>
    public string FilePath
    {
        get => (string)GetValue(FilePathProperty);
        set => SetValue(FilePathProperty, value);
    }

    /// <summary>文件扩展路径</summary>
    public string LongPath
    {
        get => (string)GetValue(LongPathProperty);
        set => SetValue(LongPathProperty, value);
    }

    /// <summary>图标边长，像素</summary>
    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public ImageSource? FallbackIcon
    {
        get => (ImageSource?)GetValue(FallbackIconProperty);
        set => SetValue(FallbackIconProperty, value);
    }

    public ISizeIconSource? FallbackIconSource
    {
        get => (ISizeIconSource?)GetValue(FallbackIconSourceProperty);
        set => SetValue(FallbackIconSourceProperty, value);
    }

    public ImageSource? FallbackIconMedium
    {
        get => (ImageSource?)GetValue(FallbackIconMediumProperty);
        set => SetValue(FallbackIconMediumProperty, value);
    }

    public bool UseThumbnail
    {
        get => (bool)GetValue(UseThumbnailProperty);
        set => SetValue(UseThumbnailProperty, value);
    }

    // 有 FallbackIconSource 就按当前显示器缩放取物理像素尺寸的图标，避免 150% / 200% 下发虚
    private ImageSource? EffectiveFallback
    {
        get
        {
            if (FallbackIconSource is { } sizeSource)
            {
                int size = PhysicalIconSize();
                if (size > 0 && sizeSource.GetIconForSize(size) is { } sized)
                {
                    return sized;
                }
            }

            return IconSize <= 64 && FallbackIconMedium is not null ? FallbackIconMedium : FallbackIcon;
        }
    }

    private int PhysicalIconSize()
    {
        double scale = DpiHelper.ScaleFor(this);
        return DpiHelper.PhysicalPixels(IconSize, scale);
    }

    /// <inheritdoc/>
    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Loaded += (_, _) => Restart(force: true);
        Unloaded += (_, _) => Cancel();
    }

    private static void OnSourcePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ThumbnailImage image)
        {
            image.Restart(force: false);
        }
    }

    private static void OnFallbackIconChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ThumbnailImage image && image.Source is null)
        {
            image.Source = image.EffectiveFallback;
        }
    }

    // force 为 true 时忽略「请求键未变」的短路（容器重新进入可视区要重新发起被取消的请求）
    private void Restart(bool force)
    {
        int size = PhysicalIconSize();
        if (!UseThumbnail || string.IsNullOrEmpty(FilePath) || size <= 0)
        {
            Cancel();
            _currentKey = string.Empty;
            Source = EffectiveFallback;
            return;
        }

        if (!ThumbnailService.IsImageExtension(PathHelper.GetExtension(FilePath)))
        {
            Cancel();
            _currentKey = string.Empty;
            Source = EffectiveFallback;
            return;
        }

        string requestKey = FilePath + "\u0001" + size;
        if (!force && string.Equals(requestKey, _currentKey, StringComparison.Ordinal))
        {
            return;
        }

        Cancel();
        _currentKey = requestKey;
        Source = EffectiveFallback;

        ImageSource? cached = ThumbnailService.Instance.TryGetCached(FilePath, size);
        if (cached is not null)
        {
            Source = cached;
            return;
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = LoadAsync(FilePath, LongPath, size, requestKey, cts);
    }

    private async Task LoadAsync(string displayPath, string longPath, int size, string requestKey, CancellationTokenSource cts)
    {
        try
        {
            ImageSource? image = await ThumbnailService.Instance
                .GetAsync(displayPath, longPath, size, cts.Token)
                .ConfigureAwait(true);

            // 期间属性变了 / 容器被回收：丢弃结果
            if (cts.IsCancellationRequested
                || !ReferenceEquals(_cts, cts)
                || !string.Equals(requestKey, _currentKey, StringComparison.Ordinal))
            {
                return;
            }

            if (image is not null)
            {
                Source = image;
            }
        }
        catch (Exception)
        {
            // 失败保持系统大图标，不打扰用户
        }
    }

    private void Cancel()
    {
        if (_cts is null)
        {
            return;
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放，忽略
        }

        _cts.Dispose();
        _cts = null;
    }
}
