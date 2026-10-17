using System.Windows.Media;
using ChangJing.App.Core.LongPath;
using ChangJing.App.Core.Operations;
using ChangJing.App.Services;

namespace ChangJing.App.ViewModels;

/// <summary>「复制到…/移动到…」里的一个源条目：普通路径 + 是否文件夹</summary>
public sealed class CopyMoveSourceItem
{
    public CopyMoveSourceItem(string displayPath, bool isDirectory)
    {
        DisplayPath = displayPath;
        IsDirectory = isDirectory;
    }

    /// <summary>源条目普通路径</summary>
    public string DisplayPath { get; }

    public bool IsDirectory { get; }
}

/// <summary>「复制到…/移动到…」里的一个快捷目标：最近使用 / 已知文件夹 / 驱动器</summary>
public sealed class CopyMoveTargetItem
{
    public CopyMoveTargetItem(
        string displayName,
        string displayPath,
        string groupHeader,
        ImageSource? icon,
        bool isAvailable,
        ulong freeSpaceBytes = 0,
        bool hasFreeSpace = false,
        bool isDrive = false)
    {
        DisplayName = displayName;
        DisplayPath = displayPath;
        GroupHeader = groupHeader;
        Icon = icon;
        IsAvailable = isAvailable;
        FreeSpaceBytes = freeSpaceBytes;
        HasFreeSpace = hasFreeSpace;
        IsDrive = isDrive;
    }

    public string DisplayName { get; }

    /// <summary>目标目录普通路径，永不包含 \\?\</summary>
    public string DisplayPath { get; }

    public string GroupHeader { get; }

    public ImageSource? Icon { get; }

    public bool IsAvailable { get; }

    /// <summary>可用空间，单位字节，未知为 0</summary>
    public ulong FreeSpaceBytes { get; }

    public bool HasFreeSpace { get; }

    public bool IsDrive { get; }

    public string DetailText
    {
        get
        {
            if (!IsAvailable)
            {
                return "不可用";
            }

            return IsDrive && HasFreeSpace
                ? $"可用 {FileItemViewModel.FormatSize(FreeSpaceBytes)}"
                : string.Empty;
        }
    }

    /// <summary>悬停提示：完整路径</summary>
    public string ToolTip => DisplayPath;

    /// <inheritdoc/>
    public override string ToString() => DisplayName;
}

/// <summary>空间需求估算探针：默认访问磁盘，验证时可换 mock</summary>
public sealed class CopyMoveSpaceProbe
{
    public static CopyMoveSpaceProbe Default { get; } = new();

    /// <summary>统计源条目占用的字节数，失败返回 0</summary>
    public Func<CopyMoveSourceItem, ulong> MeasureSourceBytes { get; init; } = MeasureFromDisk;

    /// <summary>取目标目录所在卷的可用空间，失败返回 0</summary>
    public Func<string, ulong> GetFreeSpace { get; init; } = FreeSpaceFromDisk;

    public Func<string, string, bool> IsSameVolume { get; init; } = SameVolumeOnDisk;

    private static ulong MeasureFromDisk(CopyMoveSourceItem source)
    {
        try
        {
            string longPath = PathHelper.ToLong(source.DisplayPath);
            if (OperationScanner.TryScan(
                    longPath,
                    source.IsDirectory,
                    CancellationToken.None,
                    out ScanTotals totals,
                    out _,
                    out _))
            {
                return (ulong)Math.Max(0, totals.TotalBytes);
            }
        }
        catch
        {
            // 扫描失败按 0 处理，不阻断对话框
        }

        return 0;
    }

    private static ulong FreeSpaceFromDisk(string displayPath)
    {
        try
        {
            return OperationScanner.GetAvailableFreeSpace(PathHelper.ToLong(displayPath));
        }
        catch
        {
            return 0;
        }
    }

    private static bool SameVolumeOnDisk(string sourceDisplayPath, string targetDisplayPath)
    {
        try
        {
            return TransferVolume.IsSameVolume(
                PathHelper.ToLong(sourceDisplayPath),
                PathHelper.ToLong(targetDisplayPath));
        }
        catch
        {
            return false;
        }
    }
}
