using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ChangJing.App.Core.Shell;
using ChangJing.App.Services.Preview;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Controls;

/// <summary>系统预览处理器加载完成的事件参数</summary>
public sealed class SystemPreviewLoadedEventArgs : EventArgs
{
    public required SystemPreviewResult Result { get; init; }

    public required PreviewTarget Target { get; init; }
}

/// <summary>用 HwndHost 托管系统预览处理器，如 Office / msg / svg</summary>
// 处理器跑在后台 STA 线程，宿主是独立 HWND 会盖住 WPF 弹出层，弹出前要 SetHostVisible 隐藏
public sealed class SystemPreviewHost : HwndHost
{
    private IntPtr _childHwnd;
    private SystemPreviewSession? _session;
    private PreviewTarget? _current;
    private int _lastWidth;
    private int _lastHeight;

    public event EventHandler<SystemPreviewLoadedEventArgs>? LoadCompleted;

    public Guid LastHandlerClsid => _session?.LastHandlerClsid ?? Guid.Empty;

    public bool IsHostCreated => _childHwnd != IntPtr.Zero;

    public SystemPreviewStatus LastStatus { get; private set; } = SystemPreviewStatus.NoHandler;

    public bool IsHandlerLoaded => _session?.IsLoaded == true;

    /// <inheritdoc/>
    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _childHwnd = PreviewHandlerNativeMethods.CreateWindowExW(
            0,
            "static",
            "ChangJingPreviewHost",
            PreviewHandlerNativeMethods.WsChild
            | PreviewHandlerNativeMethods.WsVisible
            | PreviewHandlerNativeMethods.WsClipChildren
            | PreviewHandlerNativeMethods.WsClipSiblings,
            0,
            0,
            0,
            0,
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        return new HandleRef(this, _childHwnd);
    }

    /// <inheritdoc/>
    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        UnloadSession();

        if (_childHwnd != IntPtr.Zero)
        {
            PreviewHandlerNativeMethods.DestroyWindow(_childHwnd);
            _childHwnd = IntPtr.Zero;
        }
    }

    /// <summary>显示或隐藏宿主窗口，弹出层打开时隐藏以规避 HwndHost 空域问题</summary>
    public void SetHostVisible(bool visible)
    {
        if (_childHwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            PreviewStaNativeMethods.ShowWindowW(_childHwnd, visible ? 5 : 0);
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(SystemPreviewHost), $"切换预览宿主可见性失败：{ex.Message}");
        }
    }

    /// <summary>在宿主上加载系统预览处理器，无处理器时返回 NoHandler</summary>
    public async Task<SystemPreviewResult> LoadAsync(PreviewTarget target, bool darkTheme)
    {
        ArgumentNullException.ThrowIfNull(target);

        _current = target;
        UnloadSession();

        if (_childHwnd == IntPtr.Zero)
        {
            // HwndHost 子窗口首次布局才创建，强制布局后再取句柄
            UpdateLayout();
        }

        if (_childHwnd == IntPtr.Zero)
        {
            return SystemPreviewResult.CreateFailed(Guid.Empty, "预览宿主尚未创建。");
        }

        // 无处理器时不必开 STA 线程
        if (PreviewHandlerRegistry.Find(Core.LongPath.PathHelper.GetExtension(target.DisplayPath)) is null)
        {
            return SystemPreviewResult.NoHandler;
        }

        var session = new SystemPreviewSession();
        _session = session;

        int width = Math.Max(1, (int)ActualWidth);
        int height = Math.Max(1, (int)ActualHeight);
        _lastWidth = width;
        _lastHeight = height;

        IntPtr hostHwnd = _childHwnd;
        string displayPath = target.DisplayPath;
        string longPath = target.LongPath;

        SystemPreviewResult result = await Task.Run(
            () => session.TryLoad(hostHwnd, displayPath, longPath, width, height, darkTheme)).ConfigureAwait(true);

        LastStatus = result.Status;

        // 目标或会话已被替换的迟到结果直接丢弃
        if (!ReferenceEquals(_session, session) || !ReferenceEquals(_current, target))
        {
            session.Dispose();
            return result;
        }

        if (!result.IsLoaded)
        {
            UnloadSession();
        }

        LoadCompleted?.Invoke(this, new SystemPreviewLoadedEventArgs { Result = result, Target = target });
        return result;
    }

    public void Unload()
    {
        _current = null;
        UnloadSession();
    }

    /// <inheritdoc/>
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        int width = Math.Max(1, (int)ActualWidth);
        int height = Math.Max(1, (int)ActualHeight);

        if (width == _lastWidth && height == _lastHeight)
        {
            return;
        }

        _lastWidth = width;
        _lastHeight = height;
        _session?.Resize(width, height);
    }

    private void UnloadSession()
    {
        SystemPreviewSession? session = _session;
        _session = null;

        if (session is null)
        {
            return;
        }

        try
        {
            session.Dispose();
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(SystemPreviewHost), $"释放预览会话失败：{ex.Message}");
        }
    }
}
