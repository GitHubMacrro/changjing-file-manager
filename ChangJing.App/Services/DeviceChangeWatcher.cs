using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ChangJing.App.Core.FileSystem;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Services;

/// <summary>驱动器热插拔监听：在窗口上挂 WM_DEVICECHANGE，去抖后回调</summary>
// 主窗口自己监听，本类供对话框等其它窗口复用
public sealed class DeviceChangeWatcher : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action _onChanged;
    private readonly DispatcherTimer _timer;

    private HwndSource? _source;
    private string _pending = string.Empty;

    public DeviceChangeWatcher(Dispatcher dispatcher, Action onChanged)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));

        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        _timer.Tick += OnTick;
    }

    /// <summary>在窗口上挂接，窗口句柄就绪后调用</summary>
    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (PresentationSource.FromVisual(window) is HwndSource source)
        {
            Attach(source);
        }
        else
        {
            LogManager.Warning(typeof(DeviceChangeWatcher), "窗口句柄未就绪，无法监听设备变更。");
        }
    }

    public void Attach(HwndSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
        source.AddHook(WndProc);
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;

        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WmDeviceChange)
        {
            return IntPtr.Zero;
        }

        int evt = wParam.ToInt32();
        if (evt is not (NativeMethods.DbtDeviceArrival or NativeMethods.DbtDeviceRemoveComplete))
        {
            return IntPtr.Zero;
        }

        if (!TryReadVolumeMask(lParam, out uint mask) || mask == 0)
        {
            return IntPtr.Zero;
        }

        _pending = DescribeDriveLetters(mask);
        _timer.Stop();
        _timer.Start();
        return IntPtr.Zero;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        try
        {
            _onChanged();
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(DeviceChangeWatcher), ex);
        }
    }

    private static bool TryReadVolumeMask(IntPtr lParam, out uint mask)
    {
        mask = 0;
        if (lParam == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var header = Marshal.PtrToStructure<NativeMethods.DEV_BROADCAST_HDR>(lParam);
            if (header.dbch_devicetype != NativeMethods.DbtDevTypVolume)
            {
                return false;
            }

            mask = Marshal.PtrToStructure<NativeMethods.DEV_BROADCAST_VOLUME>(lParam).dbcv_unitmask;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string DescribeDriveLetters(uint mask)
    {
        var letters = new System.Text.StringBuilder();
        for (int i = 0; i < 26; i++)
        {
            if ((mask & (1u << i)) != 0)
            {
                letters.Append((char)('A' + i)).Append(':');
            }
        }

        return letters.Length == 0 ? "（未知盘符）" : letters.ToString();
    }
}
