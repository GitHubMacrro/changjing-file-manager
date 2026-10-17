using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChangJing.App.Services;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Controls;

/// <summary>标题栏图标：按「小图标尺寸 × DPI」选内嵌图层，避免放大发虚</summary>
public static class AppIconProvider
{
    /// <summary>标题栏图标的目标边长，单位 DIP</summary>
    public const double TitleBarIconSize = 16.0;

    /// <summary>测试用，固定缩放比</summary>
    public static double? ScaleOverride { get; set; }

    /// <summary>测试用，指定图标文件路径</summary>
    public static string? IconPathOverride { get; set; }

    private static readonly Uri EmbeddedIconUri =
        new("pack://application:,,,/ChangJing;component/app.ico", UriKind.Absolute);

    private static readonly object Sync = new();
    private static IconBitmapDecoder? _decoder;
    private static bool _loadAttempted;

    public static ImageSource? GetTitleBarIcon(double scale = 0)
    {
        if (scale <= 0)
        {
            scale = ScaleOverride is { } over && over > 0 ? over : DpiHelper.SystemScale;
        }

        if (scale <= 0)
        {
            scale = 1.0;
        }

        int targetPixels = Math.Max(1, (int)Math.Round(TitleBarIconSize * scale));
        BitmapFrame? frame = SelectFrame(targetPixels);
        return frame;
    }

    public static int LastSelectedPixelWidth { get; private set; }

    public static IReadOnlyList<int> FramePixelWidths
    {
        get
        {
            IconBitmapDecoder? decoder = EnsureDecoder();
            if (decoder is null)
            {
                return Array.Empty<int>();
            }

            var widths = new List<int>(decoder.Frames.Count);
            foreach (BitmapFrame frame in decoder.Frames)
            {
                widths.Add(frame.PixelWidth);
            }

            return widths;
        }
    }

    private static BitmapFrame? SelectFrame(int targetPixels)
    {
        IconBitmapDecoder? decoder = EnsureDecoder();
        if (decoder is null || decoder.Frames.Count == 0)
        {
            LastSelectedPixelWidth = 0;
            return null;
        }

        BitmapFrame? best = null;
        BitmapFrame? largest = null;

        foreach (BitmapFrame frame in decoder.Frames)
        {
            if (largest is null || frame.PixelWidth > largest.PixelWidth)
            {
                largest = frame;
            }

            // 取不小于目标像素的最小图层
            if (frame.PixelWidth >= targetPixels
                && (best is null || frame.PixelWidth < best.PixelWidth))
            {
                best = frame;
            }
        }

        BitmapFrame? chosen = best ?? largest;
        LastSelectedPixelWidth = chosen?.PixelWidth ?? 0;
        return chosen;
    }

    private static IconBitmapDecoder? EnsureDecoder()
    {
        lock (Sync)
        {
            if (_loadAttempted)
            {
                return _decoder;
            }

            _loadAttempted = true;

            string? path = ResolveIconPath();
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    using var stream = File.OpenRead(path!);
                    _decoder = DecodeFrozen(stream);
                    return _decoder;
                }
                catch (Exception ex)
                {
                    LogManager.Warning(typeof(AppIconProvider), $"加载应用图标失败：{ex.Message}");
                }
            }

            // 单文件发布时输出目录没有散装 app.ico，只能从程序集里取
            try
            {
                Stream? resource = Application.GetResourceStream(EmbeddedIconUri)?.Stream;
                if (resource is null)
                {
                    LogManager.Warning(
                        typeof(AppIconProvider),
                        $"找不到应用图标文件：磁盘「{path ?? "(null)"}」与内嵌资源均不可用。");
                    return null;
                }

                using (resource)
                {
                    _decoder = DecodeFrozen(resource);
                    return _decoder;
                }
            }
            catch (Exception ex)
            {
                LogManager.Warning(typeof(AppIconProvider), $"加载内嵌应用图标失败：{ex.Message}");
                return null;
            }
        }
    }

    // Freeze 后才能缓存 / 跨线程用
    private static IconBitmapDecoder DecodeFrozen(Stream stream)
    {
        var decoder = new IconBitmapDecoder(
            stream,
            BitmapCreateOptions.None,
            BitmapCacheOption.OnLoad);

        foreach (BitmapFrame frame in decoder.Frames)
        {
            frame.Freeze();
        }

        return decoder;
    }

    private static string? ResolveIconPath()
    {
        if (!string.IsNullOrEmpty(IconPathOverride))
        {
            return IconPathOverride;
        }

        try
        {
            string baseDirectory = AppContext.BaseDirectory;
            string candidate = Path.Combine(baseDirectory, "app.ico");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(AppIconProvider), $"定位 app.ico 失败：{ex.Message}");
        }

        return null;
    }

    /// <summary>换 IconPathOverride 后调用</summary>
    public static void ResetForTest()
    {
        lock (Sync)
        {
            _decoder = null;
            _loadAttempted = false;
            LastSelectedPixelWidth = 0;
        }
    }
}
