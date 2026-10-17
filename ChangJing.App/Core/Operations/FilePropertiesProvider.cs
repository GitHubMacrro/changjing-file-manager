using System.Runtime.InteropServices;
using ChangJing.App.Core.FileSystem;
using ChangJing.App.Core.LongPath;
using ChangJing.App.Services;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Core.Operations;

/// <summary>一个条目的只读属性快照</summary>
// 字段来自一次 GetFileAttributesExW，不打开文件
public sealed class FilePropertiesSnapshot
{
    /// <summary>名称，保留结尾点与空格</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>完整普通路径</summary>
    public string DisplayPath { get; init; } = string.Empty;

    /// <summary>扩展路径</summary>
    public string LongPath { get; init; } = string.Empty;

    /// <summary>普通路径长度，单位字符</summary>
    public int PathLength { get; init; }

    public bool IsDirectory { get; init; }

    public string TypeName { get; init; } = string.Empty;

    /// <summary>文件大小，单位字节，文件夹为 0</summary>
    public ulong SizeBytes { get; init; }

    public uint Attributes { get; init; }

    public DateTime CreationTime { get; init; }

    public DateTime LastWriteTime { get; init; }

    public DateTime LastAccessTime { get; init; }

    /// <summary>路径是否超长，超过 259</summary>
    public bool IsTooLong => PathLength > LongPathRules.MaxExplorerPathLength;

    /// <summary>属性文字，中文顿号分隔</summary>
    public string AttributesText => DescribeAttributes(Attributes, IsDirectory);

    /// <summary>把 Win32 属性位翻成中文</summary>
    public static string DescribeAttributes(uint attributes, bool isDirectory)
    {
        // FILE_ATTRIBUTE_NORMAL 是占位值，避免出现正常、只读
        if (attributes == NativeMethods.FileAttributeNormal)
        {
            return "正常";
        }

        var parts = new List<string>(8);

        if ((attributes & NativeMethods.FileAttributeReadOnly) != 0)
        {
            parts.Add("只读");
        }

        if ((attributes & NativeMethods.FileAttributeHidden) != 0)
        {
            parts.Add("隐藏");
        }

        if ((attributes & NativeMethods.FileAttributeSystem) != 0)
        {
            parts.Add("系统");
        }

        if ((attributes & NativeMethods.FileAttributeArchive) != 0)
        {
            parts.Add("存档");
        }

        if ((attributes & NativeMethods.FileAttributeTemporary) != 0)
        {
            parts.Add("临时");
        }

        if ((attributes & NativeMethods.FileAttributeReparsePoint) != 0)
        {
            parts.Add("链接 / 重解析点");
        }

        if ((attributes & NativeMethods.FileAttributeCompressed) != 0)
        {
            parts.Add("压缩");
        }

        if ((attributes & NativeMethods.FileAttributeEncrypted) != 0)
        {
            parts.Add("加密");
        }

        if ((attributes & NativeMethods.FileAttributeNotContentIndexed) != 0)
        {
            parts.Add("不索引内容");
        }

        if (parts.Count == 0)
        {
            return isDirectory ? "文件夹" : "正常";
        }

        return string.Join('、', parts);
    }
}

/// <summary>读取属性并统计文件夹大小，可取消</summary>
public static class FilePropertiesProvider
{
    /// <summary>读取条目属性快照，失败返回 null</summary>
    public static FilePropertiesSnapshot? TryRead(string displayPath)
    {
        if (string.IsNullOrWhiteSpace(displayPath))
        {
            return null;
        }

        string longPath;
        try
        {
            longPath = PathHelper.ToLong(displayPath);
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(FilePropertiesProvider), $"属性查询失败（路径不可用）：{ex.Message}");
            return null;
        }

        try
        {
            if (!NativeMethods.GetFileAttributesExW(
                    longPath,
                    NativeMethods.GetFileExInfoStandard,
                    out NativeMethods.WIN32_FILE_ATTRIBUTE_DATA data))
            {
                int code = Marshal.GetLastWin32Error();
                LogManager.Warning(
                    typeof(FilePropertiesProvider),
                    $"属性查询失败：{displayPath} — {Win32ErrorMessages.Describe(code)}（错误码 {code}）");
                return null;
            }

            uint attributes = data.dwFileAttributes;
            bool isDirectory = (attributes & NativeMethods.FileAttributeDirectory) != 0;
            string name = PathHelper.GetName(PathHelper.ToDisplay(displayPath));
            string display = PathHelper.ToDisplay(longPath);

            return new FilePropertiesSnapshot
            {
                Name = name,
                DisplayPath = display,
                LongPath = longPath,
                PathLength = display.Length,
                IsDirectory = isDirectory,
                TypeName = SystemIconService.GetTypeName(name, isDirectory),
                SizeBytes = isDirectory ? 0UL : data.Size,
                Attributes = attributes,
                CreationTime = ToLocal(data.CreationTimeUtc),
                LastWriteTime = ToLocal(data.LastWriteTimeUtc),
                LastAccessTime = ToLocal(data.LastAccessTimeUtc),
            };
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(FilePropertiesProvider), ex);
            return null;
        }
    }

    /// <summary>递归统计文件夹大小，取消或失败返回 null</summary>
    public static (int FileCount, long TotalBytes)? TryMeasureFolder(
        string longPath,
        CancellationToken cancellationToken,
        out string error)
    {
        error = string.Empty;

        if (!OperationScanner.TryScan(
                longPath,
                isDirectory: true,
                cancellationToken,
                out ScanTotals totals,
                out bool canceled,
                out string scanError))
        {
            if (!canceled)
            {
                error = scanError;
            }

            return null;
        }

        return (totals.FileCount, totals.TotalBytes);
    }

    private static DateTime ToLocal(DateTime utc)
        => utc == DateTime.MinValue ? DateTime.MinValue : utc.ToLocalTime();
}
