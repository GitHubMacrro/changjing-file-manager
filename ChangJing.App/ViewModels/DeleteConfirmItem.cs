using System.Windows.Media;
using ChangJing.App.Core.Operations;
using ChangJing.App.Core.RecycleBin;
using ChangJing.App.Services;

namespace ChangJing.App.ViewModels;

/// <summary>删除确认对话框里的一项</summary>
public sealed class DeleteConfirmItem
{
    /// <summary>名称，保留结尾点 / 空格</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>完整普通路径</summary>
    public string DisplayPath { get; init; } = string.Empty;

    public bool IsDirectory { get; init; }

    /// <summary>类型名，如「文本文档」「文件夹」</summary>
    public string TypeName { get; init; } = string.Empty;

    /// <summary>大小，单位字节，文件夹为内部文件合计</summary>
    public long SizeBytes { get; init; }

    /// <summary>文件夹内的文件数，文件恒为 0</summary>
    public int ItemCount { get; init; }

    /// <summary>创建时间，本地时区</summary>
    public DateTime CreationTime { get; init; }

    /// <summary>修改时间，本地时区</summary>
    public DateTime LastWriteTime { get; init; }

    /// <summary>图标，48px 系统图标</summary>
    public ImageSource? Icon { get; init; }

    public string SizeText
    {
        get
        {
            string size = FormatSize(SizeBytes);
            if (IsDirectory && ItemCount > 0)
            {
                return $"包含 {ItemCount} 个项目，{size}";
            }

            return size;
        }
    }

    public string CreationTimeText => FormatTime(CreationTime);

    public string LastWriteTimeText => FormatTime(LastWriteTime);

    /// <summary>明细行，供单项确认框显示</summary>
    public string DetailLine
        => $"类型：{TypeName}    大小：{SizeText}{Environment.NewLine}" +
           $"创建时间：{CreationTimeText}    修改时间：{LastWriteTimeText}";

    /// <summary>由删除计划条目创建</summary>
    public static DeleteConfirmItem FromPlanItem(DeletePlanItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        string name = item.Name;
        bool isDirectory = item.IsDirectory;
        DateTime creation = DateTime.MinValue;
        DateTime modified = DateTime.MinValue;

        FilePropertiesSnapshot? snapshot = FilePropertiesProvider.TryRead(item.SourceDisplayPath);
        if (snapshot is not null)
        {
            name = snapshot.Name;
            isDirectory = snapshot.IsDirectory;
            creation = snapshot.CreationTime;
            modified = snapshot.LastWriteTime;
        }

        return new DeleteConfirmItem
        {
            Name = name,
            DisplayPath = item.SourceDisplayPath,
            IsDirectory = isDirectory,
            TypeName = SystemIconService.GetTypeName(name, isDirectory),
            SizeBytes = item.SizeBytes,
            ItemCount = item.ItemCount,
            CreationTime = creation,
            LastWriteTime = modified,
            Icon = SystemIconService.GetIconForSize(name, isDirectory, 48),
        };
    }

    /// <summary>由回收站条目创建</summary>
    public static DeleteConfirmItem FromRecycleBinEntry(RecycleBinEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new DeleteConfirmItem
        {
            Name = entry.Name,
            DisplayPath = entry.OriginalPath,
            IsDirectory = entry.IsDirectory,
            TypeName = SystemIconService.GetTypeName(entry.Name, entry.IsDirectory),
            SizeBytes = entry.SizeBytes,
            ItemCount = entry.IsDirectory ? entry.ItemCount : 0,
            CreationTime = DateTime.MinValue,
            LastWriteTime = DateTime.MinValue,
            Icon = SystemIconService.GetIconForSize(entry.Name, entry.IsDirectory, 48),
        };
    }

    /// <summary>把字节数转成人话</summary>
    public static string FormatSize(long bytes) => DeletePlanner.FormatSize(bytes);

    private static string FormatTime(DateTime value)
        => value == DateTime.MinValue ? "—" : value.ToString("yyyy/M/d HH:mm");
}
