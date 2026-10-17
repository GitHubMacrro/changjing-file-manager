using System.Runtime.InteropServices;
using ChangJing.App.Core.FileSystem;
using ChangJing.App.Core.LongPath;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Services;

/// <summary>用系统默认程序打开文件的结果</summary>
public readonly struct ShellOpenResult
{
    private ShellOpenResult(bool success, string error, int processId)
    {
        Success = success;
        Error = error;
        ProcessId = processId;
    }

    public bool Success { get; }

    /// <summary>失败原因；成功时为空串</summary>
    public string Error { get; }

    /// <summary>新启动进程的 PID；未启动新进程时为 0</summary>
    public int ProcessId { get; }

    internal static ShellOpenResult Ok(int processId) => new(true, string.Empty, processId);

    internal static ShellOpenResult Failed(string error) => new(false, error, 0);
}

/// <summary>用系统默认程序打开文件</summary>
// 普通路径须 ≤259，特殊路径 Shell 与 COM 组件处理不了会静默失败
public static class ShellOpener
{
    // SHELLEXECUTEINFO.fMask
    private const uint SeeMaskNoCloseProcess = 0x0000_0040;
    private const uint SeeMaskNoAsync = 0x0000_0100;
    private const uint SeeMaskFlagNoUi = 0x0000_0400;

    private const int SwShownormal = 1;

    /// <summary>判断该路径是否可用系统默认程序打开，检查长度与特殊名称</summary>
    public static bool CanOpenWithShell(string displayPath, out string reason)
        => !LongPathRules.TryDescribeIssue(displayPath, out reason);

    /// <summary>用系统默认程序打开 displayPath</summary>
    public static bool TryOpen(string displayPath, out ShellOpenResult result)
        => TryOpenVerb(displayPath, "open", out result);

    /// <summary>用指定 Shell 动词打开 displayPath，如 open、openas、properties</summary>
    public static bool TryOpenVerb(string displayPath, string verb, out ShellOpenResult result)
    {
        result = ShellOpenResult.Failed("路径为空。");

        if (string.IsNullOrWhiteSpace(displayPath))
        {
            return false;
        }

        if (PathHelper.IsExtended(displayPath))
        {
            // 防御：调用方应传普通路径
            displayPath = PathHelper.ToDisplay(displayPath);
        }

        var info = new SHELLEXECUTEINFOW
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFOW>(),
            fMask = SeeMaskNoCloseProcess | SeeMaskNoAsync | SeeMaskFlagNoUi,
            hwnd = IntPtr.Zero,
            lpVerb = string.IsNullOrEmpty(verb) ? "open" : verb,
            lpFile = displayPath,
            lpParameters = null,
            lpDirectory = null,
            nShow = SwShownormal,
            hInstApp = IntPtr.Zero,
            lpIDList = IntPtr.Zero,
            lpClass = null,
            hkeyClass = IntPtr.Zero,
            dwHotKey = 0,
            hIcon = IntPtr.Zero,
            hProcess = IntPtr.Zero,
        };

        try
        {
            if (!ShellExecuteExW(ref info))
            {
                int error = Marshal.GetLastWin32Error();
                string message = DescribeShellError(error);
                LogManager.Warning(
                    typeof(ShellOpener),
                    $"Shell 动词 {verb} 失败：{displayPath} — {message}（错误码 {error}）");
                result = ShellOpenResult.Failed(message);
                return false;
            }

            int processId = 0;
            if (info.hProcess != IntPtr.Zero)
            {
                try
                {
                    processId = GetProcessId(info.hProcess);
                }
                finally
                {
                    CloseHandle(info.hProcess);
                }
            }

            LogManager.Info(typeof(ShellOpener), $"已用 Shell 动词 {verb} 打开：{displayPath}（PID {processId}）");
            result = ShellOpenResult.Ok(processId);
            return true;
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(ShellOpener), ex);
            result = ShellOpenResult.Failed("调用系统打开方式时发生错误，详细信息已写入日志。");
            return false;
        }
    }

    // SE_ERR_* 是 0..32，不是 Win32 错误码，所以单独映射
    private static string DescribeShellError(int error)
    {
        return error switch
        {
            0 => "系统未能打开该文件。",
            2 => "找不到指定的文件。",
            3 => "找不到指定的路径。",
            5 => "没有权限打开该文件。",
            8 => "内存不足，无法打开该文件。",
            26 => "文件正被其他程序占用，无法共享打开。",
            27 => "文件关联信息不完整或无效。",
            28 => "打开操作超时（DDE 等待超时）。",
            29 => "DDE 事务失败。",
            30 => "DDE 事务忙，请稍后重试。",
            31 => "没有可用于打开该文件的程序（未建立文件关联）。",
            32 => "无法加载所需的动态链接库。",
            _ => Win32ErrorMessages.Describe(error),
        };
    }

    // 与 Win32 SHELLEXECUTEINFOW 布局一致
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFOW
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpVerb;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpFile;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpParameters;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpDirectory;

        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpClass;

        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteExW(ref SHELLEXECUTEINFOW lpExecInfo);

    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern int GetProcessId(IntPtr process);

    [DllImport("kernel32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
