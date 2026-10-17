using System.ComponentModel;
using System.Runtime.InteropServices;
using ChangJing.App.Core.LongPath;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Core.FileSystem;

public sealed class DirectoryEnumerationResult
{
    private DirectoryEnumerationResult(bool success, int errorCode, string error, int itemCount, bool canceled)
    {
        Success = success;
        ErrorCode = errorCode;
        Error = error;
        ItemCount = itemCount;
        Canceled = canceled;
    }

    /// <summary>是否完整枚举成功</summary>
    public bool Success { get; }

    /// <summary>Win32 错误码</summary>
    public int ErrorCode { get; }

    /// <summary>失败原因，中文；成功时为空串</summary>
    public string Error { get; }

    /// <summary>已回调的条目数</summary>
    public int ItemCount { get; }

    public bool Canceled { get; }

    internal static DirectoryEnumerationResult Ok(int itemCount)
        => new(true, 0, string.Empty, itemCount, canceled: false);

    internal static DirectoryEnumerationResult CanceledResult(int itemCount)
        => new(false, 0, "已取消。", itemCount, canceled: true);

    internal static DirectoryEnumerationResult Failed(int errorCode, string error, int itemCount)
        => new(false, errorCode, error, itemCount, canceled: false);
}

/// <summary>基于 FindFirstFileExW 的目录枚举器</summary>
public sealed class DirectoryEnumerator
{
    /// <summary>默认批量回调大小，单位条</summary>
    public const int DefaultBatchSize = 500;

    /// <summary>批量回调大小，单位条</summary>
    public int BatchSize { get; }

    /// <summary>是否跳过隐藏与系统条目</summary>
    public bool SkipHiddenAndSystem { get; }

    public DirectoryEnumerator(int batchSize = DefaultBatchSize, bool skipHiddenAndSystem = false)
    {
        BatchSize = batchSize > 0 ? batchSize : DefaultBatchSize;
        SkipHiddenAndSystem = skipHiddenAndSystem;
    }

    /// <summary>枚举目录下的条目；不抛异常</summary>
    public DirectoryEnumerationResult Enumerate(
        string longDirectoryPath,
        Action<IReadOnlyList<FileEntry>> onBatch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onBatch);

        // \\?\ 路径不做规范化，格式得自己保证：目录带尾分隔符再拼 *
        string searchRoot;
        string searchPattern;
        try
        {
            searchRoot = PathHelper.EnsureTrailingSeparator(PathHelper.ToLong(longDirectoryPath));
            searchPattern = searchRoot + "*";
        }
        catch (ArgumentException ex)
        {
            LogManager.Warning(typeof(DirectoryEnumerator), $"枚举失败（路径不可用）：{longDirectoryPath} — {ex.Message}");
            return DirectoryEnumerationResult.Failed(-1, ex.Message, 0);
        }

        SafeFindHandle handle = NativeMethods.FindFirstFileExW(
            searchPattern,
            NativeMethods.FindExInfoBasic,
            out NativeMethods.WIN32_FIND_DATAW findData,
            NativeMethods.FindExSearchNameMatch,
            IntPtr.Zero,
            NativeMethods.FindFirstExLargeFetch);

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();

            string message = ErrorText(error);
            LogManager.Warning(
                typeof(DirectoryEnumerator),
                $"无法枚举目录 {searchRoot}：{message}（错误码 {error}）");

            return DirectoryEnumerationResult.Failed(error, message, 0);
        }

        int itemCount = 0;
        var batch = new List<FileEntry>(BatchSize);

        try
        {
            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    if (batch.Count > 0)
                    {
                        itemCount += batch.Count;
                        onBatch(batch);
                    }

                    LogManager.Info(typeof(DirectoryEnumerator), $"枚举已取消：{searchRoot}（已回调 {itemCount} 条）");
                    return DirectoryEnumerationResult.CanceledResult(itemCount);
                }

                string name = findData.cFileName;
                if (!string.IsNullOrEmpty(name) && name != "." && name != "..")
                {
                    uint attributes = findData.dwFileAttributes;
                    bool skip = SkipHiddenAndSystem
                                && ((attributes & NativeMethods.FileAttributeHidden) != 0
                                    || (attributes & NativeMethods.FileAttributeSystem) != 0);

                    if (!skip)
                    {
                        bool isDirectory = (attributes & NativeMethods.FileAttributeDirectory) != 0;
                        batch.Add(new FileEntry(
                            name,
                            searchRoot + name,
                            attributes,
                            isDirectory ? 0UL : findData.Size,
                            findData.LastWriteTimeUtc));
                    }
                }

                if (batch.Count >= BatchSize)
                {
                    itemCount += batch.Count;
                    onBatch(batch);
                    batch = new List<FileEntry>(BatchSize);
                }

                if (!NativeMethods.FindNextFileW(handle, out findData))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (!Win32ErrorMessages.IsEndOfEnumeration(error))
                    {
                        // 中途失败：保留已回调的结果
                        if (batch.Count > 0)
                        {
                            itemCount += batch.Count;
                            onBatch(batch);
                        }

                        string message = ErrorText(error);
                        LogManager.Warning(
                            typeof(DirectoryEnumerator),
                            $"枚举中断 {searchRoot}：{message}（错误码 {error}，已回调 {itemCount} 条）");

                        return DirectoryEnumerationResult.Failed(error, message, itemCount);
                    }

                    break;
                }
            }

            if (batch.Count > 0)
            {
                itemCount += batch.Count;
                onBatch(batch);
            }

            return DirectoryEnumerationResult.Ok(itemCount);
        }
        catch (Exception ex)
        {
            // 只可能是回调内部抛出的异常，不向调用方抛
            LogManager.Error(typeof(DirectoryEnumerator), ex);
            return DirectoryEnumerationResult.Failed(-1, "读取文件夹时发生错误，详细信息已写入日志。", itemCount);
        }
        finally
        {
            handle.Dispose();
        }
    }

    public (IReadOnlyList<FileEntry> Items, DirectoryEnumerationResult Result) ToList(
        string longDirectoryPath,
        CancellationToken cancellationToken = default)
    {
        var items = new List<FileEntry>();
        DirectoryEnumerationResult result = Enumerate(
            longDirectoryPath,
            batch => items.AddRange(batch),
            cancellationToken);

        return (items, result);
    }

    /// <summary>生成错误提示：中文文案 + 系统消息</summary>
    private static string ErrorText(int errorCode)
    {
        string message = Win32ErrorMessages.Describe(errorCode);

        try
        {
            string systemMessage = new Win32Exception(errorCode).Message;
            if (!string.IsNullOrEmpty(systemMessage))
            {
                message += $"（{systemMessage}）";
            }
        }
        catch
        {
        }

        return message;
    }
}
