using ChangJing.App.Core.FileSystem;
using ChangJing.App.Core.LongPath;
using ChangJing.App.Core.Operations;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Core.RecycleBin;

/// <summary>一个条目的删除去向</summary>
public enum DeleteDisposition
{
    /// <summary>移到程序自有回收站，可撤销</summary>
    RecycleBin,

    /// <summary>永久删除，不可撤销，已由界面确认</summary>
    PermanentDelete,

    /// <summary>单项超出回收站容量上限，需要问用户是否永久删除</summary>
    TooLargeForRecycleBin,
}

/// <summary>删除计划里的一个条目</summary>
public sealed class DeletePlanItem
{
    /// <summary>条目普通路径</summary>
    public string SourceDisplayPath { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public bool IsDirectory { get; init; }

    /// <summary>大小按字节：文件为自身大小，文件夹为内部合计</summary>
    public long SizeBytes { get; init; }

    /// <summary>文件夹内的文件数，文件恒为 0</summary>
    public int ItemCount { get; init; }

    /// <summary>所在卷根，普通路径</summary>
    public string VolumeRoot { get; init; } = string.Empty;

    public DriveKind DriveKind { get; init; }

    public DeleteDisposition Disposition { get; set; }

    /// <summary>去向原因，中文界面提示用</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>是否因不可回收的卷而永久删除，如可移动盘 / 网络盘 / 光驱</summary>
    public bool ExcludedVolume { get; set; }

    /// <summary>是否因为「单项超过回收站容量上限」而需要确认</summary>
    public bool TooLarge { get; set; }
}

/// <summary>一次删除请求的计划</summary>
public sealed class DeletePlan
{
    /// <summary>计划条目，顺序与请求一致</summary>
    public IReadOnlyList<DeletePlanItem> Items { get; init; } = Array.Empty<DeletePlanItem>();

    public IEnumerable<DeletePlanItem> ToRecycleBin
        => Items.Where(item => item.Disposition == DeleteDisposition.RecycleBin);

    public IEnumerable<DeletePlanItem> ToPermanentDelete
        => Items.Where(item => item.Disposition == DeleteDisposition.PermanentDelete);

    /// <summary>超出容量上限、需要用户确认的条目</summary>
    public IEnumerable<DeletePlanItem> TooLarge
        => Items.Where(item => item.Disposition == DeleteDisposition.TooLargeForRecycleBin);

    /// <summary>是否存在不可移动盘 / 网络盘导致的永久删除</summary>
    public bool HasExcludedVolumeItems => Items.Any(item => item.ExcludedVolume);

    public bool IsEmpty => Items.Count == 0;
}

/// <summary>删除去向规划：判断条目应移入回收站还是永久删除</summary>
public sealed class DeletePlanner
{
    private readonly RecycleBinStore _store;
    private readonly Func<string, DriveKind> _driveKindProbe;

    /// <summary>创建规划器，驱动器类型走真实磁盘查询</summary>
    public DeletePlanner(RecycleBinStore store)
        : this(store, VolumeProbe.GetDriveKind)
    {
    }

    public DeletePlanner(RecycleBinStore store, Func<string, DriveKind> driveKindProbe)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _driveKindProbe = driveKindProbe ?? throw new ArgumentNullException(nameof(driveKindProbe));
    }

    public DeletePlanItem? PlanItem(
        string displayPath,
        bool forcePermanent,
        CancellationToken cancellationToken)
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
            LogManager.Warning(typeof(DeletePlanner), $"删除规划失败（路径不可用）：{ex.Message}");
            return null;
        }

        if (!FileOperations.TryGetEntryInfo(longPath, out uint attributes, out ulong size, out _))
        {
            LogManager.Warning(typeof(DeletePlanner), $"删除规划失败（条目不存在）：{displayPath}");
            return null;
        }

        bool isDirectory = (attributes & NativeMethods.FileAttributeDirectory) != 0;
        string volumeRoot = VolumeProbe.GetMountRoot(PathHelper.ToDisplay(longPath));
        DriveKind driveKind = _driveKindProbe(volumeRoot);

        (long measured, int itemCount) = Measure(longPath, isDirectory, size, cancellationToken);

        var item = new DeletePlanItem
        {
            SourceDisplayPath = PathHelper.ToDisplay(longPath),
            Name = PathHelper.GetName(longPath),
            IsDirectory = isDirectory,
            SizeBytes = measured,
            ItemCount = itemCount,
            VolumeRoot = volumeRoot,
            DriveKind = driveKind,
        };

        if (forcePermanent)
        {
            item.Disposition = DeleteDisposition.PermanentDelete;
            item.Reason = "已设置为删除时不进回收站。";
            return item;
        }

        if (VolumeProbe.IsRecycleBinExcluded(driveKind))
        {
            item.Disposition = DeleteDisposition.PermanentDelete;
            item.ExcludedVolume = true;
            item.Reason = "此位置的文件将被永久删除。";
            return item;
        }

        long limit = _store.GetCapacityLimit(volumeRoot);
        if (limit > 0 && measured > limit)
        {
            item.Disposition = DeleteDisposition.TooLargeForRecycleBin;
            item.TooLarge = true;
            item.Reason = $"此项目太大（{FormatSize(measured)}），超出回收站容量上限（{FormatSize(limit)}）。";
            return item;
        }

        item.Disposition = DeleteDisposition.RecycleBin;
        item.Reason = string.Empty;
        return item;
    }

    /// <summary>为一组条目规划去向，跳过失败条目并记日志</summary>
    public DeletePlan Plan(
        IReadOnlyList<string> displayPaths,
        bool forcePermanent,
        CancellationToken cancellationToken = default)
    {
        var items = new List<DeletePlanItem>();

        foreach (string path in displayPaths)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            DeletePlanItem? item = PlanItem(path, forcePermanent, cancellationToken);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        return new DeletePlan { Items = items };
    }

    private static (long Bytes, int ItemCount) Measure(string longPath, bool isDirectory, ulong size, CancellationToken cancellationToken)
    {
        if (!isDirectory)
        {
            return (size > long.MaxValue ? long.MaxValue : (long)size, 0);
        }

        // 根是重解析点：只算链接自身不枚举目标，指祖先会无限递归
        if (DeleteEngine.IsReparsePoint(longPath))
        {
            return (0, 0);
        }

        if (!OperationScanner.TryScan(
                longPath,
                isDirectory: true,
                cancellationToken,
                out ScanTotals totals,
                out _,
                out _))
        {
            return (0, 0);
        }

        return (totals.TotalBytes, totals.FileCount);
    }

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 字节";
        }

        string[] units = { "字节", "KB", "MB", "GB", "TB", "PB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} 字节" : $"{value:0.#} {units[unit]}";
    }
}
