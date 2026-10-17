using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Core.Shell;

/// <summary>专用后台 STA 线程：为系统预览处理器提供单线程单元 + 消息泵</summary>
// COM 对象必须在 STA 线程创建调用，且要一直泵消息
internal sealed class PreviewStaThread : IDisposable
{
    private const int WmAppRunPending = 0x8000 + 1; // WM_APP + 1
    private static readonly IntPtr HwndMessage = PreviewStaNativeMethods.HwndMessage;

    private readonly ConcurrentQueue<Action> _pending = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ManualResetEventSlim _stopped = new(false);
    private readonly Thread _thread;

    private IntPtr _messageWindow;
    private uint _threadId;

    /// <summary>创建并启动 STA 线程，等待就绪</summary>
    public PreviewStaThread()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ChangJing.PreviewHandler.STA",
        };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>该 STA 线程的线程 ID，供处理器 / 诊断</summary>
    public uint ThreadId => _threadId;

    /// <summary>把工作项投递到 STA 线程执行，立即返回不等待</summary>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _pending.Enqueue(action);

        IntPtr window = _messageWindow;
        if (window == IntPtr.Zero)
        {
            return;
        }

        PreviewStaNativeMethods.PostMessageW(window, WmAppRunPending, IntPtr.Zero, IntPtr.Zero);
    }

    private void Run()
    {
        try
        {
            PreviewHandlerNativeMethods.CoInitializeEx(IntPtr.Zero, PreviewHandlerNativeMethods.CoinitApartmentThreaded);
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(PreviewStaThread), $"预览 STA 线程初始化 COM 失败：{ex.Message}");
        }

        _threadId = PreviewStaNativeMethods.GetCurrentThreadId();

        try
        {
            _messageWindow = PreviewHandlerNativeMethods.CreateWindowExW(
                0,
                "static",
                "ChangJingPreviewSta",
                0,
                0,
                0,
                0,
                0,
                HwndMessage,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(PreviewStaThread), $"创建预览 STA 消息窗口失败：{ex.Message}");
            _messageWindow = IntPtr.Zero;
        }

        _ready.Set();

        try
        {
            while (PreviewStaNativeMethods.GetMessageW(out PreviewMsg msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WmAppRunPending)
                {
                    while (_pending.TryDequeue(out Action? action))
                    {
                        try
                        {
                            action();
                        }
                        catch (Exception ex)
                        {
                            LogManager.Error(typeof(PreviewStaThread), ex);
                        }
                    }

                    continue;
                }

                PreviewStaNativeMethods.TranslateMessage(ref msg);
                PreviewStaNativeMethods.DispatchMessageW(ref msg);
            }
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(PreviewStaThread), ex);
        }
        finally
        {
            try
            {
                if (_messageWindow != IntPtr.Zero)
                {
                    PreviewHandlerNativeMethods.DestroyWindow(_messageWindow);
                    _messageWindow = IntPtr.Zero;
                }

                PreviewHandlerNativeMethods.CoUninitialize();
            }
            catch (Exception ex)
            {
                LogManager.Debug(typeof(PreviewStaThread), $"预览 STA 线程清理失败：{ex.Message}");
            }

            _stopped.Set();
        }
    }

    /// <summary>请求线程退出并短暂等待，超时即放弃</summary>
    public void Dispose()
    {
        try
        {
            IntPtr window = _messageWindow;
            if (window != IntPtr.Zero)
            {
                PreviewStaNativeMethods.PostMessageW(window, PreviewStaNativeMethods.WmQuit, IntPtr.Zero, IntPtr.Zero);
            }
            else
            {
                PreviewStaNativeMethods.PostThreadMessageW(_threadId, PreviewStaNativeMethods.WmQuit, IntPtr.Zero, IntPtr.Zero);
            }

            _stopped.Wait(TimeSpan.FromSeconds(1));
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(PreviewStaThread), $"停止预览 STA 线程失败：{ex.Message}");
        }
    }
}
