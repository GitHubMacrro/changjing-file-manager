using ChangJing.App.Core.FileSystem;
using ChangJing.App.Core.LongPath;

namespace ChangJing.App.Core.Operations;

public readonly struct OperationPreflightResult
{
    private OperationPreflightResult(bool isValid, string error)
    {
        IsValid = isValid;
        Error = error;
    }

    public bool IsValid { get; }

    /// <summary>未通过时的中文原因</summary>
    public string Error { get; }

    public static OperationPreflightResult Ok() => new(true, string.Empty);

    public static OperationPreflightResult Invalid(string error) => new(false, error);
}

/// <summary>开始前同步预检，不建文件即判必然失败</summary>
// 空间检查扫元数据，不读内容，提前提示不足
internal static class OperationPreflight
{
    /// <summary>预检任务，任一项不过即返回原因</summary>
    public static OperationPreflightResult Check(OperationJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (job.Sources.Count == 0)
        {
            return OperationPreflightResult.Invalid("没有要处理的项目。");
        }

        bool isDelete = job.Kind == OperationKind.Delete;
        bool isMove = job.Kind == OperationKind.Move;
        string verb = isMove ? "移动" : isDelete ? "删除" : "复制";

        // 每个目标卷需占用字节，删除不需要
        var bytesPerVolume = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (OperationSource source in job.Sources)
        {
            string sourceLong;
            try
            {
                sourceLong = PathHelper.ToLong(source.SourceDisplayPath);
            }
            catch
            {
                return OperationPreflightResult.Invalid($"源路径无效：{source.SourceDisplayPath}");
            }

            if (!FileOperations.TryGetEntryInfo(sourceLong, out uint attributes, out _, out _))
            {
                return OperationPreflightResult.Invalid($"源文件已不存在：{source.SourceDisplayPath}");
            }

            bool isDirectory = (attributes & NativeMethods.FileAttributeDirectory) != 0;

            // 永久删除没有目标位置，不做目标 / 空间检查
            if (isDelete)
            {
                continue;
            }

            string targetLong;
            try
            {
                targetLong = PathHelper.ToLong(source.TargetDirectoryDisplayPath);
            }
            catch
            {
                return OperationPreflightResult.Invalid("目标位置不可用。");
            }

            if (!FileOperations.TryGetEntryInfo(targetLong, out uint targetAttributes, out _, out _))
            {
                return OperationPreflightResult.Invalid("目标位置不存在或不可用。");
            }

            if ((targetAttributes & NativeMethods.FileAttributeDirectory) == 0)
            {
                return OperationPreflightResult.Invalid("目标位置不是文件夹。");
            }

            // 不允许把文件夹复制 / 移动到自身或其子文件夹
            if (isDirectory && IsSameOrDescendant(targetLong, sourceLong))
            {
                return OperationPreflightResult.Invalid($"无法把文件夹{verb}到其自身或子文件夹：{PathHelper.GetName(sourceLong)}");
            }

            // 统计需占用空间，移动仅跨卷需要
            bool needsBytes = !isMove;
            if (isMove && !job.Options.ForceCrossVolume && TransferVolume.IsSameVolume(sourceLong, targetLong))
            {
                needsBytes = false;
            }

            if (needsBytes)
            {
                if (!OperationScanner.TryScan(
                        sourceLong,
                        isDirectory,
                        CancellationToken.None,
                        out ScanTotals totals,
                        out _,
                        out _))
                {
                    // 扫描失败不在此阻断，交给执行阶段记录
                    continue;
                }

                string mount = TransferVolume.GetMountRoot(targetLong);
                if (mount.Length == 0)
                {
                    mount = "::unknown";
                }

                bytesPerVolume[mount] = bytesPerVolume.TryGetValue(mount, out long existing)
                    ? existing + totals.TotalBytes
                    : totals.TotalBytes;
            }
        }

        foreach ((string mount, long needed) in bytesPerVolume)
        {
            if (needed <= 0 || mount == "::unknown")
            {
                continue;
            }

            ulong free = OperationScanner.GetAvailableFreeSpace(mount);
            if (free > 0 && free < (ulong)needed)
            {
                return OperationPreflightResult.Invalid(
                    $"目标磁盘空间不足：需要 {FormatSize(needed)}，可用 {FormatSize((long)free)}，无法{verb}。");
            }
        }

        return OperationPreflightResult.Ok();
    }

    private static bool IsSameOrDescendant(string candidateLong, string ancestorLong)
    {
        string candidate = PathHelper.Normalize(candidateLong);
        string ancestor = PathHelper.Normalize(ancestorLong);

        if (string.Equals(candidate, ancestor, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string prefix = ancestor.EndsWith('\\') ? ancestor : ancestor + "\\";
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }
}
