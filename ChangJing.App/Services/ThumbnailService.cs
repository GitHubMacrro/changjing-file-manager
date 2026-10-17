using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChangJing.App.Core.FileSystem;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Services;

/// <summary>图片缩略图服务：用 IShellItemImageFactory 取资源管理器同款缩略图，失败时用 WIC 通过扩展路径解码兜底</summary>
// 后台线程执行，最多 4 个并发；LRU 缓存上限约 500 张
public sealed class ThumbnailService
{
    /// <summary>进程内单例，界面各处共用一份缓存</summary>
    public static ThumbnailService Instance { get; } = new();

    private const int MaxCacheEntries = 500;
    private const int MaxConcurrency = 4;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".ico",
    };

    private readonly object _cacheLock = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly SemaphoreSlim _gate = new(MaxConcurrency, MaxConcurrency);

    private ThumbnailService()
    {
    }

    public static bool IsImageExtension(string extension)
        => !string.IsNullOrEmpty(extension) && ImageExtensions.Contains(extension);

    /// <summary>把请求尺寸归到缓存档，避免每种像素各存一份</summary>
    public static string SizeBucket(int size) => size <= 32 ? "s" : size <= 64 ? "m" : size <= 160 ? "l" : "x";

    /// <summary>取缓存中的缩略图；没有则返回 null</summary>
    public ImageSource? TryGetCached(string displayPath, int size)
    {
        string key = BuildKey(displayPath, size);
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out LinkedListNode<CacheEntry>? node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Image;
            }
        }

        return null;
    }

    /// <summary>异步取缩略图，缓存命中直接返回，取消或失败返回 null</summary>
    public async Task<ImageSource?> GetAsync(
        string displayPath,
        string longPath,
        int size,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(displayPath) || size <= 0)
        {
            return null;
        }

        string key = BuildKey(displayPath, size);
        ImageSource? cached = TryGetCached(displayPath, size);
        if (cached is not null)
        {
            return cached;
        }

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            // 双重检查：等待信号量期间可能已被别的请求填好
            cached = TryGetCached(displayPath, size);
            if (cached is not null)
            {
                return cached;
            }

            ImageSource? image = Load(displayPath, longPath, size, cancellationToken);
            if (image is not null && !cancellationToken.IsCancellationRequested)
            {
                Store(key, image);
            }

            return image;
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(ThumbnailService), $"取缩略图失败（{displayPath}）：{ex.Message}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    // 加载

    private static ImageSource? Load(string displayPath, string longPath, int size, CancellationToken cancellationToken)
    {
        ImageSource? image = TryShellThumbnail(displayPath, size);
        if (image is not null || cancellationToken.IsCancellationRequested)
        {
            return image;
        }

        // 长路径：试扩展路径，多数 Shell 会拒绝但个别可用
        if (!longPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            image = TryShellThumbnail(longPath, size);
            if (image is not null || cancellationToken.IsCancellationRequested)
            {
                return image;
            }
        }

        // WIC 解码兜底，扩展路径加 FileStream 对超长路径也有效
        return TryWicDecode(longPath, size);
    }

    private static ImageSource? TryShellThumbnail(string path, int size)
    {
        IntPtr hBitmap = IntPtr.Zero;
        object? factoryObject = null;
        try
        {
            Guid iid = typeof(ShellImageNativeMethods.IShellItemImageFactory).GUID;
            ShellImageNativeMethods.SHCreateItemFromParsingName(
                path,
                IntPtr.Zero,
                ref iid,
                out ShellImageNativeMethods.IShellItemImageFactory factory);

            factoryObject = factory;
            if (factory is null)
            {
                return null;
            }

            uint flags = ShellImageNativeMethods.SiigbfThumbnailOnly
                         | ShellImageNativeMethods.SiigbfBiggerSizeOk
                         | ShellImageNativeMethods.SiigbfCropToSquare;

            factory.GetImage(new ShellImageNativeMethods.SIZE(size, size), flags, out hBitmap);
            if (hBitmap == IntPtr.Zero)
            {
                return null;
            }

            BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            source.Freeze();
            return source;
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(ThumbnailService), $"Shell 缩略图不可用（{path}）：{ex.Message}");
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero)
            {
                ShellImageNativeMethods.DeleteObject(hBitmap);
            }

            if (factoryObject is not null)
            {
                try
                {
                    Marshal.FinalReleaseComObject(factoryObject);
                }
                catch
                {
                    // 忽略释放异常
                }
            }
        }
    }

    private static ImageSource? TryWicDecode(string longPath, int size)
    {
        try
        {
            using var stream = new FileStream(
                longPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.DecodePixelWidth = size;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(ThumbnailService), $"WIC 解码缩略图失败（{longPath}）：{ex.Message}");
            return null;
        }
    }

    // 缓存

    private static string BuildKey(string displayPath, int size) => displayPath + "\u0001" + SizeBucket(size);

    private void Store(string key, ImageSource image)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out LinkedListNode<CacheEntry>? existing))
            {
                _lru.Remove(existing);
                _cache.Remove(key);
            }

            var entry = new CacheEntry(key, image);
            LinkedListNode<CacheEntry> node = _lru.AddFirst(entry);
            _cache[key] = node;

            while (_cache.Count > MaxCacheEntries && _lru.Last is { } last)
            {
                _lru.RemoveLast();
                _cache.Remove(last.Value.Key);
            }
        }
    }

    private sealed record CacheEntry(string Key, ImageSource Image);
}
