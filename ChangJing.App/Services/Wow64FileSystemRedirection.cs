using System.Runtime.InteropServices;

namespace ChangJing.App.Services;

/// <summary>x86 进程下按作用域临时关闭 WOW64 文件系统重定向，让 System32 读到真实目录</summary>
// 重定向按线程生效，且关闭期间该线程的 LoadLibrary / COM 激活也不再重定向；
// 故只在纯 Win32 文件 API 调用前后短暂开关，作用域内不调 Shell/COM，也不跨 await
public static class Wow64FileSystemRedirection
{
    // 只有「32 位进程运行在 64 位系统」才存在文件系统重定向
    private static readonly bool Applicable = !Environment.Is64BitProcess && Environment.Is64BitOperatingSystem;

    /// <summary>当前进程是否需要处理文件系统重定向</summary>
    public static bool IsApplicable => Applicable;

    /// <summary>临时关闭当前线程的文件系统重定向，Dispose 时还原；不需要时为空操作</summary>
    public static Scope Suspend() => Applicable ? new Scope(active: true) : default;

    /// <summary>作用域：构造时关闭重定向，Dispose 时还原到进入前的状态</summary>
    // 只在调用线程生效，必须与 Suspend 同线程 Dispose，故作用域内不能跨 await / 切换线程
    public readonly struct Scope : IDisposable
    {
        private readonly IntPtr _oldValue;

        private readonly bool _active;

        internal Scope(bool active)
        {
            _oldValue = IntPtr.Zero;
            _active = false;

            if (!active)
            {
                return;
            }

            if (Wow64DisableWow64FsRedirection(out IntPtr oldValue))
            {
                _oldValue = oldValue;
                _active = true;
            }

            // 关闭失败说明状态未变，Dispose 无需还原
        }

        /// <summary>还原到进入作用域前的重定向状态</summary>
        public void Dispose()
        {
            if (_active)
            {
                Wow64RevertWow64FsRedirection(_oldValue);
            }
        }
    }

    // SetLastError 关闭：调用方紧接着读的是文件 API 的错误码，不能被这两个调用覆盖
    [DllImport("kernel32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Wow64DisableWow64FsRedirection(out IntPtr oldValue);

    [DllImport("kernel32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Wow64RevertWow64FsRedirection(IntPtr oldValue);
}
