using System.Threading.Tasks;
using ChangJing.App.Core.LongPath;
using ChangJing.App.Core.Operations;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Services;

/// <summary>位置可用性探测：无权限、驱动器移除、网络盘不可达等</summary>
// 探测在后台线程执行并带时间预算，避免界面被卡住
public static class LocationAvailability
{
    /// <summary>默认探测时间预算，单位毫秒</summary>
    public const int DefaultTimeoutMilliseconds = 3000;

    public enum ProbeStatus
    {
        /// <summary>存在且为文件夹</summary>
        Available,

        /// <summary>不存在，路径 / 驱动器不可用或被删除</summary>
        Unavailable,

        /// <summary>存在但不是文件夹，如同名文件</summary>
        NotADirectory,

        /// <summary>超出时间预算，不可达的网络路径无法判定</summary>
        TimedOut,
    }

    /// <summary>探测 displayPath 是否为可访问的文件夹</summary>
    public static ProbeStatus Probe(string? displayPath, int timeoutMilliseconds = DefaultTimeoutMilliseconds)
    {
        if (string.IsNullOrWhiteSpace(displayPath))
        {
            return ProbeStatus.Unavailable;
        }

        Task<ProbeStatus> task;
        try
        {
            task = Task.Run(() => ProbeCore(displayPath));
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(LocationAvailability), $"探测任务创建失败（{displayPath}）：{ex.Message}");
            return ProbeStatus.TimedOut;
        }

        if (timeoutMilliseconds <= 0)
        {
            timeoutMilliseconds = DefaultTimeoutMilliseconds;
        }

        try
        {
            // Wait 而不是 await：本方法由调用方在后台线程调用，不阻塞界面
            if (task.Wait(timeoutMilliseconds))
            {
                return task.Result;
            }
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(LocationAvailability), $"探测等待异常（{displayPath}）：{ex.Message}");
            return ProbeStatus.TimedOut;
        }

        return ProbeStatus.TimedOut;
    }

    private static ProbeStatus ProbeCore(string displayPath)
    {
        try
        {
            string longPath = PathHelper.ToLong(displayPath);
            if (!FileOperations.TryGetEntryInfo(longPath, out uint attributes, out _, out _))
            {
                return ProbeStatus.Unavailable;
            }

            return (attributes & Core.FileSystem.NativeMethods.FileAttributeDirectory) != 0
                ? ProbeStatus.Available
                : ProbeStatus.NotADirectory;
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(LocationAvailability), $"探测失败（{displayPath}）：{ex.Message}");
            return ProbeStatus.Unavailable;
        }
    }

    /// <summary>逐级向上找最近存在的祖先文件夹，找不到返回 null</summary>
    public static string? FindNearestExistingAncestor(
        string? displayPath,
        int timeoutMilliseconds,
        out bool timedOut)
    {
        timedOut = false;
        if (string.IsNullOrWhiteSpace(displayPath))
        {
            return null;
        }

        string? current = displayPath;
        int guard = 0;

        while (current is { Length: > 0 } && guard++ < 512)
        {
            ProbeStatus status = Probe(current, timeoutMilliseconds);
            if (status == ProbeStatus.Available)
            {
                return current;
            }

            if (status == ProbeStatus.TimedOut)
            {
                timedOut = true;
                return null;
            }

            string? parent;
            try
            {
                parent = PathHelper.GetParent(PathHelper.ToLong(current));
            }
            catch (Exception ex)
            {
                LogManager.Debug(typeof(LocationAvailability), $"取上级失败（{current}）：{ex.Message}");
                return null;
            }

            if (parent is null)
            {
                return null;
            }

            current = PathHelper.ToDisplay(parent);
        }

        return null;
    }
}
