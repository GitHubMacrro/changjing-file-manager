using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using ChangJing.App.Core.FileSystem;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Services;

/// <summary>系统剪贴板文本读写</summary>
// 不用 WPF 的 Clipboard.SetDataObject：它走 OLE，剪贴板忙时会长时间重试并卡住 UI 线程
public static class ClipboardService
{
    /// <summary>写剪贴板的总时间预算，单位毫秒，超过即放弃</summary>
    public const int TotalBudgetMs = 100;

    private const int RetryDelayMs = 15;

    private const int MaxAttempts = 5;

    /// <summary>把文本写入系统剪贴板，总耗时不超 TotalBudgetMs 毫秒</summary>
    public static bool TrySetText(string text)
    {
        if (text is null)
        {
            return false;
        }

        var stopwatch = Stopwatch.StartNew();

        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (TrySetTextOnce(text, out int errorCode))
            {
                return true;
            }

            LogManager.Warning(
                typeof(ClipboardService),
                $"写剪贴板失败（第 {attempt + 1} 次，已用 {stopwatch.ElapsedMilliseconds} ms，错误码 {errorCode}）");

            // 预算用尽或已到最大次数：立即放弃，不在 UI 线程上长时间等待
            if (attempt >= MaxAttempts - 1 || stopwatch.ElapsedMilliseconds + RetryDelayMs > TotalBudgetMs)
            {
                break;
            }

            Thread.Sleep(RetryDelayMs);
        }

        return false;
    }

    private static bool TrySetTextOnce(string text, out int errorCode)
    {
        errorCode = 0;
        IntPtr handle = IntPtr.Zero;
        bool opened = false;

        try
        {
            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                errorCode = Marshal.GetLastWin32Error();
                return false;
            }

            opened = true;

            if (!NativeMethods.EmptyClipboard())
            {
                errorCode = Marshal.GetLastWin32Error();
                return false;
            }

            // 末尾补一个 '\0'，CF_UNICODETEXT 要求以 NUL 结尾
            int byteCount = (text.Length + 1) * sizeof(char);
            handle = NativeMethods.GlobalAlloc(NativeMethods.GmemMoveable, (UIntPtr)byteCount);
            if (handle == IntPtr.Zero)
            {
                errorCode = Marshal.GetLastWin32Error();
                return false;
            }

            IntPtr target = NativeMethods.GlobalLock(handle);
            if (target == IntPtr.Zero)
            {
                errorCode = Marshal.GetLastWin32Error();
                return false;
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                Marshal.WriteInt16(target, text.Length * sizeof(char), 0);
            }
            finally
            {
                NativeMethods.GlobalUnlock(handle);
            }

            if (NativeMethods.SetClipboardData(NativeMethods.CfUnicodeText, handle) == IntPtr.Zero)
            {
                errorCode = Marshal.GetLastWin32Error();
                return false;
            }

            // 成功后内存所有权归系统剪贴板，不能再 GlobalFree
            handle = IntPtr.Zero;
            return true;
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(ClipboardService), $"写剪贴板异常：{ex.Message}");
            return false;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                NativeMethods.GlobalFree(handle);
            }

            if (opened)
            {
                NativeMethods.CloseClipboard();
            }
        }
    }
}
