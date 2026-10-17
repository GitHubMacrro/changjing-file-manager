using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChangJing.App.Core.LongPath;
using ChangJing.App.Services;
using CustomWin.Utils.Logs;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ChangJing.App.Services.Preview;

/// <summary>PDF 预览加载器，基于 Windows.Data.Pdf</summary>
public static class PdfPreviewLoader
{
    private static int _cleanupDone;

    /// <summary>PDF 临时副本目录，验证时可被覆盖</summary>
    public static string PreviewTempDirectory
        => string.IsNullOrEmpty(PreviewTempDirectoryOverride)
            ? AppPaths.PreviewTempDirectory
            : PreviewTempDirectoryOverride!;

    /// <summary>PDF 预览临时目录覆盖，仅验证用，产品不设置</summary>
    public static string? PreviewTempDirectoryOverride { get; set; }

    /// <summary>打开 PDF，短路径优先，失败改用临时副本</summary>
    public static async Task<PdfPreviewHandle?> OpenAsync(string displayPath, string longPath, CancellationToken ct)
    {
        EnsureStaleCleanup();

        try
        {
            return await Task.Run(async () =>
            {
                string? tempPath = null;

                try
                {
                    ct.ThrowIfCancellationRequested();

                    StorageFile? file = null;

                    try
                    {
                        if (!string.IsNullOrEmpty(displayPath))
                        {
                            file = await StorageFile.GetFileFromPathAsync(displayPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogManager.Debug(typeof(PdfPreviewLoader), $"短路径打开 PDF 失败，改用临时副本（{displayPath}）：{ex.Message}");
                    }

                    if (file is null)
                    {
                        tempPath = await CopyToTempAsync(longPath, displayPath, ct).ConfigureAwait(false);
                        if (tempPath is null)
                        {
                            return null;
                        }

                        file = await StorageFile.GetFileFromPathAsync(tempPath);
                    }

                    if (file is null)
                    {
                        return null;
                    }

                    ct.ThrowIfCancellationRequested();

                    PdfDocument document = await PdfDocument.LoadFromFileAsync(file);

                    int pageCount = (int)document.PageCount;
                    uint width = 0;
                    uint height = 0;

                    if (pageCount > 0)
                    {
                        PdfPage first = document.GetPage(0);
                        try
                        {
                            Windows.Foundation.Size size = first.Size;
                            width = ToUInt(size.Width);
                            height = ToUInt(size.Height);
                        }
                        finally
                        {
                            DisposeQuietly(first);
                        }
                    }

                    var info = new PdfPreview
                    {
                        PageCount = pageCount,
                        PageWidth = width,
                        PageHeight = height,
                    };

                    return new PdfPreviewHandle(info, document, tempPath);
                }
                catch (OperationCanceledException)
                {
                    DeleteQuietly(tempPath);
                    return null;
                }
                catch (Exception ex)
                {
                    LogManager.Warning(typeof(PdfPreviewLoader), $"打开 PDF 失败（{displayPath}）：{ex.Message}");
                    DeleteQuietly(tempPath);
                    return null;
                }
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(PdfPreviewLoader), $"PDF 预览任务异常（{displayPath}）：{ex.Message}");
            return null;
        }
    }

    /// <summary>清扫超过 1 天的残留副本，首次 OpenAsync 惰性执行一次</summary>
    public static void CleanupStaleTempFiles()
    {
        try
        {
            if (!Directory.Exists(PreviewTempDirectory))
            {
                return;
            }

            DateTime cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (string path in Directory.EnumerateFiles(PreviewTempDirectory))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Debug(typeof(PdfPreviewLoader), $"删除残留预览副本失败（{path}）：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(PdfPreviewLoader), $"清扫预览临时目录失败：{ex.Message}");
        }
    }

    private static void EnsureStaleCleanup()
    {
        if (Interlocked.Exchange(ref _cleanupDone, 1) != 0)
        {
            return;
        }

        CleanupStaleTempFiles();
    }

    /// <summary>删除全部残留副本，占用中的留到下次启动清扫</summary>
    public static void CleanupAll()
    {
        try
        {
            if (!Directory.Exists(PreviewTempDirectory))
            {
                return;
            }

            foreach (string path in Directory.EnumerateFiles(PreviewTempDirectory))
            {
                DeleteQuietly(path);
            }
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(PdfPreviewLoader), $"清理预览临时目录失败：{ex.Message}");
        }
    }

    private static async Task<string?> CopyToTempAsync(string longPath, string displayPath, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(PreviewTempDirectory);

            string destination = Path.Combine(PreviewTempDirectory, Guid.NewGuid().ToString("N") + SafeExtension(displayPath));

            await Task.Run(() =>
            {
                if (string.IsNullOrEmpty(longPath))
                {
                    throw new FileNotFoundException("缺少文件路径。", longPath);
                }

                using var source = new FileStream(
                    longPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                source.CopyTo(target);
            }, ct).ConfigureAwait(false);

            return destination;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(PdfPreviewLoader), $"创建 PDF 临时副本失败（{displayPath}）：{ex.Message}");
            return null;
        }
    }

    private static string SafeExtension(string displayPath)
    {
        string extension = PathHelper.GetExtension(displayPath);
        if (extension.Length is < 2 or > 11)
        {
            return ".pdf";
        }

        for (int i = 1; i < extension.Length; i++)
        {
            if (!char.IsLetterOrDigit(extension[i]))
            {
                return ".pdf";
            }
        }

        return extension;
    }

    private static uint ToUInt(double value)
        => value <= 0 ? 0u : (uint)Math.Round(value);

    internal static void DeleteQuietly(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(PdfPreviewLoader), $"删除临时副本失败（{path}）：{ex.Message}");
        }
    }

    internal static void DisposeQuietly(object? instance)
    {
        if (instance is IDisposable disposable)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                LogManager.Debug(typeof(PdfPreviewLoader), $"释放 PDF 资源失败：{ex.Message}");
            }
        }
    }
}

/// <summary>已打开的 PDF：静态信息 + 运行期句柄，可按页渲染</summary>
public sealed class PdfPreviewHandle : IDisposable
{
    private readonly PdfDocument? _document;
    private bool _disposed;

    internal PdfPreviewHandle(PdfPreview info, PdfDocument? document, string? tempFilePath)
    {
        Info = info;
        _document = document;
        TempFilePath = tempFilePath;
    }

    /// <summary>静态信息：页数 / 首页尺寸</summary>
    public PdfPreview Info { get; }

    /// <summary>非空表示需要清理的临时副本路径</summary>
    public string? TempFilePath { get; }

    /// <summary>句柄是否仍可用于渲染</summary>
    public bool IsValid => _document is not null && !_disposed;

    /// <summary>渲染指定页为已 Freeze 的 ImageSource，失败返回 null</summary>
    public async Task<ImageSource?> RenderPageAsync(int pageIndex, int decodePixelWidth, CancellationToken ct)
    {
        PdfDocument? document = _document;
        if (document is null || _disposed || pageIndex < 0 || pageIndex >= Info.PageCount)
        {
            return null;
        }

        try
        {
            return await Task.Run(async () =>
            {
                ct.ThrowIfCancellationRequested();

                PdfPage page = document.GetPage((uint)pageIndex);
                try
                {
                    using var ras = new InMemoryRandomAccessStream();

                    var options = new PdfPageRenderOptions
                    {
                        DestinationWidth = (uint)Math.Max(64, decodePixelWidth),
                    };

                    await page.RenderToStreamAsync(ras, options);

                    ct.ThrowIfCancellationRequested();

                    ras.Seek(0);

                    using Stream netStream = ras.AsStreamForRead();
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    bitmap.StreamSource = netStream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return (ImageSource?)bitmap;
                }
                finally
                {
                    PdfPreviewLoader.DisposeQuietly(page);
                }
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(PdfPreviewLoader), $"渲染 PDF 第 {pageIndex + 1} 页失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>释放句柄并删除临时副本，忽略失败</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        PdfPreviewLoader.DisposeQuietly(_document);
        PdfPreviewLoader.DeleteQuietly(TempFilePath);
    }
}
