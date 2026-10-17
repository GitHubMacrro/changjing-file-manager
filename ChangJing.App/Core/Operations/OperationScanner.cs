using System.Runtime.InteropServices;
using ChangJing.App.Core.FileSystem;
using ChangJing.App.Core.LongPath;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Core.Operations;

/// <summary>扫描结果：文件总数与总字节数</summary>
public readonly struct ScanTotals
{
    internal ScanTotals(int fileCount, long totalBytes)
    {
        FileCount = fileCount;
        TotalBytes = totalBytes;
    }

    /// <summary>文件总数，不含文件夹</summary>
    public int FileCount { get; }

    public long TotalBytes { get; }
}

/// <summary>执行前递归统计，供进度与剩余估算</summary>
// 重解析点不下钻，避免无限递归
internal static class OperationScanner
{
    /// <summary>统计一个条目，取消时 <paramref name="canceled"/> 为 true</summary>
    public static bool TryScan(
        string longPath,
        bool isDirectory,
        CancellationToken cancellationToken,
        out ScanTotals totals,
        out bool canceled,
        out string error)
    {
        long bytes = 0;
        int files = 0;
        canceled = false;
        error = string.Empty;

        if (!isDirectory)
        {
            if (!NativeMethods.GetFileAttributesExW(
                    longPath,
                    NativeMethods.GetFileExInfoStandard,
                    out NativeMethods.WIN32_FILE_ATTRIBUTE_DATA data))
            {
                int code = Marshal.GetLastWin32Error();
                error = Win32ErrorMessages.Describe(code);
                totals = default;
                return false;
            }

            totals = new ScanTotals(1, (long)data.Size);
            return true;
        }

        var enumerator = new DirectoryEnumerator();
        var stack = new Stack<string>();
        stack.Push(longPath);

        while (stack.Count > 0)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                totals = new ScanTotals(files, bytes);
                return false;
            }

            string dir = stack.Pop();

            var (items, result) = enumerator.ToList(dir, cancellationToken);
            if (result.Canceled)
            {
                canceled = true;
                totals = new ScanTotals(files, bytes);
                return false;
            }

            if (!result.Success)
            {
                // 子目录枚举失败记录错误，交给调用方
                error = result.Error;
                totals = new ScanTotals(files, bytes);
                return false;
            }

            foreach (FileEntry entry in items)
            {
                if (entry.IsDirectory)
                {
                    if (!entry.IsReparsePoint)
                    {
                        stack.Push(entry.FullPath);
                    }

                    continue;
                }

                files++;
                bytes += (long)entry.Size;
            }
        }

        totals = new ScanTotals(files, bytes);
        return true;
    }

    /// <summary>取目标卷可用空间，单位字节，失败为 0</summary>
    public static ulong GetAvailableFreeSpace(string longPath)
    {
        try
        {
            string probe = PathHelper.ToLong(longPath);
            if (NativeMethods.GetDiskFreeSpaceExW(probe, out ulong free, out _, out _))
            {
                return free;
            }
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(OperationScanner), $"查询可用空间失败（{PathHelper.ToDisplay(longPath)}）：{ex.Message}");
        }

        return 0;
    }
}
