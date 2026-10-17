using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using ChangJing.App.Core.LongPath;
using ChangJing.App.Core.Shell;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Services.Preview;

public enum SystemPreviewStatus
{
    NoHandler,

    Loaded,

    Timeout,

    Failed,
}

public sealed record SystemPreviewResult(SystemPreviewStatus Status, Guid HandlerClsid, string Message)
{
    public bool IsLoaded => Status == SystemPreviewStatus.Loaded;

    public static SystemPreviewResult NoHandler { get; } = new(SystemPreviewStatus.NoHandler, Guid.Empty, string.Empty);

    public static SystemPreviewResult CreateTimeout(Guid clsid)
        => new(SystemPreviewStatus.Timeout, clsid, "预览处理器响应超时。");

    public static SystemPreviewResult CreateFailed(Guid clsid, string message)
        => new(SystemPreviewStatus.Failed, clsid, message);
}

/// <summary>系统预览会话：专用后台 STA 线程驱动 IPreviewHandler，崩溃只影响该线程</summary>
public sealed class SystemPreviewSession : IDisposable
{
    /// <summary>默认加载超时，单位毫秒</summary>
    public const int DefaultTimeoutMilliseconds = 3000;

    private readonly object _gate = new();
    private PreviewStaThread? _sta;
    private object? _handler;
    private IPreviewHandler? _previewHandler;
    private FileStream? _stream;
    private bool _abandoned;
    private bool _disposed;

    /// <summary>最近一次命中的处理器 CLSID</summary>
    public Guid LastHandlerClsid { get; private set; }

    public bool IsLoaded => _previewHandler is not null;

    public SystemPreviewResult TryLoad(
        IntPtr hostHwnd,
        string displayPath,
        string longPath,
        int width,
        int height,
        bool darkTheme,
        int timeoutMilliseconds = DefaultTimeoutMilliseconds)
    {
        if (_disposed)
        {
            return SystemPreviewResult.CreateFailed(Guid.Empty, "预览会话已释放。");
        }

        string extension = PathHelper.GetExtension(displayPath);
        PreviewHandlerRegistration? registration = PreviewHandlerRegistry.Find(extension);
        if (registration is null)
        {
            return SystemPreviewResult.NoHandler;
        }

        LastHandlerClsid = registration.Clsid;

        PreviewStaThread sta = EnsureStaThread();

        SystemPreviewResult? result = null;
        using var done = new ManualResetEventSlim(false);

        sta.Post(() =>
        {
            try
            {
                result = LoadCore(registration, hostHwnd, displayPath, longPath, width, height, darkTheme);
            }
            catch (Exception ex)
            {
                LogManager.Error(typeof(SystemPreviewSession), ex);
                result = SystemPreviewResult.CreateFailed(registration.Clsid, "无法加载预览处理器。");
            }
            finally
            {
                done.Set();
            }
        });

        if (!done.Wait(timeoutMilliseconds))
        {
            // 处理器挂死：放弃这条 STA 线程，下次新建一条
            AbandonStaThread();
            return SystemPreviewResult.CreateTimeout(registration.Clsid);
        }

        return result ?? SystemPreviewResult.CreateFailed(registration.Clsid, "预览处理器未返回结果。");
    }

    public void Resize(int width, int height)
    {
        PreviewStaThread? sta;
        IPreviewHandler? handler;
        lock (_gate)
        {
            sta = _sta;
            handler = _previewHandler;
        }

        if (sta is null || handler is null)
        {
            return;
        }

        var rect = PreviewRect.FromSize(width, height);
        sta.Post(() =>
        {
            try
            {
                handler.SetRect(ref rect);
            }
            catch (Exception ex)
            {
                LogManager.Debug(typeof(SystemPreviewSession), $"调整预览区域失败：{ex.Message}");
            }
        });
    }

    public void Unload()
    {
        PreviewStaThread? sta;
        IPreviewHandler? handler;
        object? handlerObject;
        FileStream? stream;
        lock (_gate)
        {
            sta = _sta;
            handler = _previewHandler;
            handlerObject = _handler;
            stream = _stream;
            _previewHandler = null;
            _handler = null;
            _stream = null;
        }

        if (sta is null)
        {
            // 处理器没加载成功，但可能有残留的流 / COM 对象
            DisposeStream(stream);
            ReleaseComObject(handlerObject);
            return;
        }

        using var done = new ManualResetEventSlim(false);
        sta.Post(() =>
        {
            try
            {
                handler?.Unload();
            }
            catch (Exception ex)
            {
                LogManager.Debug(typeof(SystemPreviewSession), $"卸载预览处理器失败：{ex.Message}");
            }
            finally
            {
                ReleaseComObject(handlerObject);
                done.Set();
            }
        });

        done.Wait(1000);

        // 流由本会话关闭：处理器只持有引用
        DisposeStream(stream);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unload();

        PreviewStaThread? sta;
        lock (_gate)
        {
            sta = _sta;
            _sta = null;
        }

        sta?.Dispose();
    }

    // 内部

    private PreviewStaThread EnsureStaThread()
    {
        lock (_gate)
        {
            return _sta ??= new PreviewStaThread();
        }
    }

    private void AbandonStaThread()
    {
        PreviewStaThread? sta;
        object? handlerObject;
        FileStream? stream;
        lock (_gate)
        {
            sta = _sta;
            handlerObject = _handler;
            stream = _stream;
            _sta = null;
            _previewHandler = null;
            _handler = null;
            _stream = null;
            _abandoned = true;   // LoadCore 若在超时后才完成，会自行收尾
        }

        // 不等线程，但残留流 / COM 对象必须在这里释放，否则泄漏
        DisposeStream(stream);
        ReleaseComObject(handlerObject);
        _ = sta;
    }

    private static void ReleaseComObject(object? comObject)
    {
        try
        {
            if (comObject is not null && Marshal.IsComObject(comObject))
            {
                Marshal.ReleaseComObject(comObject);
            }
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(SystemPreviewSession), $"释放预览处理器失败：{ex.Message}");
        }
    }

    private SystemPreviewResult LoadCore(
        PreviewHandlerRegistration registration,
        IntPtr hostHwnd,
        string displayPath,
        string longPath,
        int width,
        int height,
        bool darkTheme)
    {
        Guid clsid = registration.Clsid;
        Guid iidPreviewHandler = typeof(IPreviewHandler).GUID;

        uint context = PreviewHandlerRegistry.PreferLocalServer(clsid)
            ? PreviewHandlerNativeMethods.ClsctxLocalServer
            : PreviewHandlerNativeMethods.ClsctxInprocServer;

        object? instance = null;
        int hr = PreviewHandlerNativeMethods.CoCreateInstance(
            ref clsid, null, context, ref iidPreviewHandler, out instance);

        if (hr != 0 || instance is null)
        {
            LogManager.Warning(
                typeof(SystemPreviewSession),
                $"创建预览处理器失败（CLSID {clsid:B}，CLSCTX {context}，HRESULT 0x{hr:X8}）。");

            // 进程外失败时再试一次进程内，少数处理器注册形态不同
            if (context == PreviewHandlerNativeMethods.ClsctxLocalServer)
            {
                hr = PreviewHandlerNativeMethods.CoCreateInstance(
                    ref clsid, null, PreviewHandlerNativeMethods.ClsctxInprocServer, ref iidPreviewHandler, out instance);
            }

            if (hr != 0 || instance is null)
            {
                return SystemPreviewResult.CreateFailed(clsid, $"无法创建预览处理器（HRESULT 0x{hr:X8}）。");
            }
        }

        if (instance is not IPreviewHandler handler)
        {
            Marshal.ReleaseComObject(instance);
            return SystemPreviewResult.CreateFailed(clsid, "预览处理器未实现 IPreviewHandler。");
        }

        string? tempCopy = null;
        FileStream? stream = null;
        IStream? comStream = null;

        try
        {
            bool initialized = false;

            // 优先走流式初始化，不锁文件
            if (handler is IInitializeWithStream streamInit)
            {
                try
                {
                    stream = new FileStream(
                        longPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    comStream = new FileStreamIStream(stream);
                    streamInit.Initialize(comStream, PreviewHandlerNativeMethods.StgmRead);
                    initialized = true;
                }
                catch (Exception ex)
                {
                    LogManager.Debug(typeof(SystemPreviewSession), $"流式初始化预览处理器失败，尝试按文件路径：{ex.Message}");
                    DisposeStream(ref comStream, ref stream);
                }
            }

            // 处理器只支持文件路径时，长路径得先复制成短路径
            if (!initialized && handler is IInitializeWithFile fileInit)
            {
                string initPath = displayPath;
                if (longPath.Length > LongPathRules.MaxExplorerPathLength || longPath.StartsWith(@"\\?\", StringComparison.Ordinal))
                {
                    tempCopy = PreviewTempCopy.Prepare(longPath, displayPath);
                    initPath = tempCopy ?? displayPath;
                }

                fileInit.Initialize(initPath, PreviewHandlerNativeMethods.StgmRead);
                initialized = true;
            }

            if (!initialized)
            {
                Marshal.ReleaseComObject(instance);
                return SystemPreviewResult.CreateFailed(clsid, "预览处理器不支持流或文件初始化。");
            }

            // 部分处理器要求提供 site，给 null 即可
            if (handler is IObjectWithSite objectWithSite)
            {
                try
                {
                    objectWithSite.SetSite(null);
                }
                catch (Exception ex)
                {
                    LogManager.Debug(typeof(SystemPreviewSession), $"设置预览处理器 site 失败：{ex.Message}");
                }
            }

            // 深色主题：处理器支持配色接口时才设置
            if (darkTheme && handler is IPreviewHandlerVisuals visuals)
            {
                try
                {
                    visuals.SetBackgroundColor(ToColorRef(0x1E, 0x1F, 0x22));
                    visuals.SetTextColor(ToColorRef(0xE6, 0xE8, 0xEB));
                }
                catch (Exception ex)
                {
                    LogManager.Debug(typeof(SystemPreviewSession), $"设置预览处理器配色失败：{ex.Message}");
                }
            }

            // 绑定窗口并开始预览
            var rect = PreviewRect.FromSize(width, height);
            handler.SetWindow(hostHwnd, ref rect);
            handler.DoPreview();

            bool abandoned;
            lock (_gate)
            {
                abandoned = _abandoned || _disposed;
                if (!abandoned)
                {
                    _handler = instance;
                    _previewHandler = handler;
                    _stream = stream;      // 由会话持有，Unload 时随处理器一起释放
                }
            }

            if (abandoned)
            {
                // 超时后才完成：对象没入会话，只能在此收尾免得泄漏
                try
                {
                    handler.Unload();
                }
                catch (Exception ex)
                {
                    LogManager.Debug(typeof(SystemPreviewSession), $"放弃预览会话时卸载失败：{ex.Message}");
                }

                ReleaseComObject(instance);
                DisposeStream(ref comStream, ref stream);

                if (tempCopy is not null)
                {
                    PreviewTempCopy.TryDelete(tempCopy);
                }

                return SystemPreviewResult.CreateTimeout(clsid);
            }

            LogManager.Info(
                typeof(SystemPreviewSession),
                $"已用系统预览处理器（{registration.SourceKey}，CLSID {clsid:B}）加载 {displayPath}。");

            return new SystemPreviewResult(SystemPreviewStatus.Loaded, clsid, string.Empty);
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(SystemPreviewSession), $"初始化预览处理器失败：{ex.Message}");
            DisposeStream(ref comStream, ref stream);

            try
            {
                if (Marshal.IsComObject(instance))
                {
                    Marshal.ReleaseComObject(instance);
                }
            }
            catch
            {
            }

            if (tempCopy is not null)
            {
                PreviewTempCopy.TryDelete(tempCopy);
            }

            return SystemPreviewResult.CreateFailed(clsid, "初始化预览处理器失败。");
        }
    }

    private static void DisposeStream(ref IStream? comStream, ref FileStream? stream)
    {
        comStream = null;
        DisposeStream(stream);
        stream = null;
    }

    private static void DisposeStream(FileStream? stream)
    {
        try
        {
            stream?.Dispose();
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(SystemPreviewSession), $"关闭预览流失败：{ex.Message}");
        }
    }

    private static uint ToColorRef(byte r, byte g, byte b) => (uint)((b << 16) | (g << 8) | r);
}
