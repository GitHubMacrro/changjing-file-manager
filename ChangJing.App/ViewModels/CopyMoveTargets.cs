using ChangJing.App.Core.FileSystem;
using ChangJing.App.Core.LongPath;
using ChangJing.App.Services;

namespace ChangJing.App.ViewModels;

/// <summary>构建「复制到…/移动到…」对话框里的快捷目标列表</summary>
public static class CopyMoveTargets
{
    public const string GroupRecent = "最近使用";

    public const string GroupCommon = "常用位置";

    public const string GroupDrives = "此电脑";

    public const string GroupOther = "其他位置";

    private static readonly string[] CommonFolderIds = { "Desktop", "Documents", "Downloads", "Pictures" };

    /// <summary>构建快捷目标列表，按「最近使用 → 常用位置 → 此电脑」分组</summary>
    public static IReadOnlyList<CopyMoveTargetItem> Build(IReadOnlyList<string>? recentTargets)
    {
        var items = new List<CopyMoveTargetItem>();

        // 最近使用：去重，只保留仍存在且是文件夹的项
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (recentTargets is not null)
        {
            foreach (string target in recentTargets)
            {
                if (string.IsNullOrWhiteSpace(target) || !seen.Add(target))
                {
                    continue;
                }

                if (!IsAvailableDirectory(target))
                {
                    continue;
                }

                items.Add(new CopyMoveTargetItem(
                    PathHelper.GetName(PathHelper.ToLong(target)),
                    target,
                    GroupRecent,
                    SystemIconService.GetIcon("folder", isDirectory: true),
                    isAvailable: true));
            }
        }

        // 常用位置：桌面 / 文档 / 下载 / 图片
        foreach (KnownFolderItem folder in LocationProvider.GetKnownFolders())
        {
            if (Array.IndexOf(CommonFolderIds, folder.Id) < 0 || !folder.IsAvailable)
            {
                continue;
            }

            string path = folder.DisplayPath!;
            items.Add(new CopyMoveTargetItem(
                folder.DisplayName,
                path,
                GroupCommon,
                SystemIconService.GetIcon("folder", isDirectory: true),
                isAvailable: true));
        }

        // 此电脑：就绪的固定 / 可移动磁盘，显示剩余空间
        foreach (CopyMoveTargetItem drive in BuildDrives())
        {
            items.Add(drive);
        }

        return items;
    }

    /// <summary>构建「此电脑」分组项，供热插拔时刷新磁盘列表</summary>
    public static IReadOnlyList<CopyMoveTargetItem> BuildDrives()
    {
        var items = new List<CopyMoveTargetItem>();

        foreach (DriveItem drive in LocationProvider.GetDrives())
        {
            if (!drive.IsReady || drive.Kind is not (DriveKind.Fixed or DriveKind.Removable))
            {
                continue;
            }

            items.Add(new CopyMoveTargetItem(
                drive.DisplayName,
                drive.RootPath,
                GroupDrives,
                SystemIconService.GetDriveIcon(),
                isAvailable: true,
                freeSpaceBytes: drive.FreeSpaceBytes,
                hasFreeSpace: drive.HasSizeInfo,
                isDrive: true));
        }

        return items;
    }

    private static bool IsAvailableDirectory(string displayPath)
    {
        try
        {
            string longPath = PathHelper.ToLong(displayPath);
            return NativeMethods.GetFileAttributesExW(
                       longPath,
                       NativeMethods.GetFileExInfoStandard,
                       out NativeMethods.WIN32_FILE_ATTRIBUTE_DATA data)
                   && (data.dwFileAttributes & NativeMethods.FileAttributeDirectory) != 0;
        }
        catch
        {
            return false;
        }
    }
}
