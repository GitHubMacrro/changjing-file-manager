using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Services;

/// <summary>全局异常的用户提示：写日志 + 弹出友好中文对话框</summary>
// 对话框只能在 UI 线程建，同种异常 60 秒内只弹一次
public static class ErrorReporter
{
    /// <summary>同一异常的去重时间窗口，窗口内重复出现不再弹窗</summary>
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(60);

    // 同一时刻最多一个提示框，避免异常风暴刷屏
    private static int _dialogOpen;

    // 去重表：异常指纹 → 记录
    private static readonly ConcurrentDictionary<string, DuplicateRecord> Seen = new(StringComparer.Ordinal);

    // 去重表上限，防止异常种类无限增长导致内存泄漏
    private const int MaxTrackedSignatures = 256;

    /// <summary>记录并提示一个未处理异常，任何提示失败都不会向外抛出</summary>
    public static void ReportUnhandled(Exception ex, string context, bool showDialog = true)
    {
        string signature = BuildSignature(ex);

        try
        {
            LogManager.Error(typeof(ErrorReporter), $"[{context}] 未处理异常（指纹 {signature}）");
            LogManager.Error(typeof(ErrorReporter), ex);
        }
        catch
        {
            // 记日志失败不能再抛，否则会掩盖原始异常
        }

        if (!showDialog)
        {
            return;
        }

        // 时间窗口内同一异常只弹一次
        if (!ShouldShow(signature, out int suppressedBefore))
        {
            if (suppressedBefore > 0)
            {
                try
                {
                    LogManager.Warning(
                        typeof(ErrorReporter),
                        $"[{context}] 同一异常在 {DuplicateWindow.TotalSeconds:0} 秒内重复出现，已抑制弹窗（累计抑制 {suppressedBefore} 次）。");
                }
                catch
                {
                    // 忽略
                }
            }

            return;
        }

        ShowFriendlyMessage(ex, context, signature);
    }

    /// <summary>在 UI 线程弹出友好提示，非 UI 线程则派发，永不抛出</summary>
    public static void ShowFriendlyMessage(Exception ex, string context)
        => ShowFriendlyMessage(ex, context, BuildSignature(ex));

    private static void ShowFriendlyMessage(Exception ex, string context, string signature)
    {
        try
        {
            Application? app = Application.Current;
            if (app is null)
            {
                ShowNativeMessage(ex, context);
                return;
            }

            if (app.Dispatcher.CheckAccess())
            {
                ShowFluentMessage(ex, context, signature);
            }
            else
            {
                app.Dispatcher.BeginInvoke(() => ShowFluentMessage(ex, context, signature));
            }
        }
        catch
        {
            // 兜底：连对话框都建不出来时不再尝试
        }
    }

    private static void ShowFluentMessage(Exception ex, string context, string signature)
    {
        if (Interlocked.CompareExchange(ref _dialogOpen, 1, 0) != 0)
        {
            // 已有提示框在显示，只记日志并释放去重标记
            Unmark(signature);
            return;
        }

        try
        {
            var messageBox = new Wpf.Ui.Controls.MessageBox
            {
                Title = "畅径遇到问题",
                Content = BuildMessage(ex, context),
                CloseButtonText = "关闭",
                IsCloseButtonEnabled = true,
                IsPrimaryButtonEnabled = false,
                IsSecondaryButtonEnabled = false,
            };

            // 非模态显示，避免异常处理路径阻塞消息循环，WPF-UI 4.3.0 已弃用同步 Show
            _ = ShowAsync(messageBox, signature);
        }
        catch
        {
            Interlocked.Exchange(ref _dialogOpen, 0);

            try
            {
                MessageBox.Show(
                    BuildMessage(ex, context),
                    "畅径遇到问题",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch
            {
                // 忽略：无法提示时不阻断进程
            }
        }
    }

    private static async Task ShowAsync(Wpf.Ui.Controls.MessageBox messageBox, string signature)
    {
        try
        {
            await messageBox.ShowDialogAsync(showAsDialog: false, cancellationToken: CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(ErrorReporter), ex);
        }
        finally
        {
            Interlocked.Exchange(ref _dialogOpen, 0);
            MarkShown(signature);
        }
    }

    private static void ShowNativeMessage(Exception ex, string context)
    {
        try
        {
            MessageBox.Show(
                BuildMessage(ex, context),
                "畅径遇到问题",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch
        {
            // 忽略
        }
    }

    // 去重

    private static bool ShouldShow(string signature, out int suppressedBefore)
    {
        suppressedBefore = 0;
        DateTime now = DateTime.UtcNow;

        DuplicateRecord record = Seen.GetOrAdd(signature, static _ => new DuplicateRecord());
        lock (record)
        {
            if (record.LastShownUtc != default && now - record.LastShownUtc < DuplicateWindow)
            {
                record.SuppressedCount++;
                suppressedBefore = record.SuppressedCount;
                return false;
            }

            record.SuppressedCount = 0;
            record.LastShownUtc = now;
            TrimIfNeeded();
            return true;
        }
    }

    private static void MarkShown(string signature)
    {
        DuplicateRecord record = Seen.GetOrAdd(signature, static _ => new DuplicateRecord());
        lock (record)
        {
            record.LastShownUtc = DateTime.UtcNow;
        }
    }

    // 已有提示框在显示，只记日志并释放去重标记
    private static void Unmark(string signature)
    {
        if (Seen.TryGetValue(signature, out DuplicateRecord? record))
        {
            lock (record)
            {
                record.LastShownUtc = default;
            }
        }
    }

    private static void TrimIfNeeded()
    {
        if (Seen.Count <= MaxTrackedSignatures)
        {
            return;
        }

        // 简单清理：移除已过时间窗口的指纹
        DateTime cutoff = DateTime.UtcNow - DuplicateWindow;
        foreach (KeyValuePair<string, DuplicateRecord> pair in Seen)
        {
            if (pair.Value.LastShownUtc != default && pair.Value.LastShownUtc < cutoff)
            {
                Seen.TryRemove(pair.Key, out _);
            }
        }
    }

    // 类型 + 消息 + 栈顶帧，都取不到时退化为类型名
    private static string BuildSignature(Exception ex)
    {
        try
        {
            string type = ex.GetType().FullName ?? ex.GetType().Name;
            string message = ex.Message ?? string.Empty;
            string frame = TopFrame(ex);
            return $"{type}|{message}|{frame}";
        }
        catch
        {
            return ex.GetType().Name;
        }
    }

    private static string TopFrame(Exception ex)
    {
        try
        {
            var trace = new StackTrace(ex, fNeedFileInfo: false);
            StackFrame? frame = trace.GetFrame(0);
            if (frame?.GetMethod() is { } method)
            {
                return $"{method.DeclaringType?.FullName}.{method.Name}";
            }
        }
        catch
        {
            // 取栈失败不影响去重，退化为类型 + 消息
        }

        return string.Empty;
    }

    private static string BuildMessage(Exception ex, string context)
    {
        return
            "抱歉，软件刚遇到一个问题。已记录到日志，你可以继续使用；" +
            "如果问题反复出现，请把日志文件发给开发者。\n\n" +
            $"位置：{context}\n" +
            $"错误：{ex.GetType().Name}\n" +
            $"{ex.Message}";
    }

    private sealed class DuplicateRecord
    {
        // 上次弹窗时间 UTC，default 表示还没弹过
        public DateTime LastShownUtc { get; set; }

        // 本时间窗口内已被抑制的次数
        public int SuppressedCount { get; set; }
    }
}
