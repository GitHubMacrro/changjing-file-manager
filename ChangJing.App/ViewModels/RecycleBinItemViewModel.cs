using System.Windows.Media;
using ChangJing.App.Core.RecycleBin;
using ChangJing.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChangJing.App.ViewModels;

/// <summary>回收站页面可排序的列</summary>
public enum RecycleBinSortColumn
{
    /// <summary>名称，自然排序</summary>
    Name,

    OriginalLocation,

    DeletedDate,

    Size,

    Type,
}

/// <summary>回收站页面里的一行：名称、原位置、删除日期、大小、类型 + 图标</summary>
public sealed class RecycleBinItemViewModel
{
    public RecycleBinItemViewModel(RecycleBinEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Entry = entry;
        Name = entry.Name;
        OriginalPath = entry.OriginalPath;
        IsDirectory = entry.IsDirectory;
        DeletedLocal = entry.DeletedLocal;
        SizeBytes = Math.Max(0, entry.SizeBytes);
        TypeName = SystemIconService.GetTypeName(entry.Name, entry.IsDirectory);
        SizeText = entry.IsDirectory
            ? $"{(entry.ItemCount > 0 ? entry.ItemCount : 0)} 个项目，{FormatSize(SizeBytes)}"
            : FormatSize(SizeBytes);
        Icon = SystemIconService.GetIcon(entry.Name, entry.IsDirectory);
    }

    /// <summary>对应的回收站条目，还原 / 永久删除时使用</summary>
    public RecycleBinEntry Entry { get; }

    /// <summary>名称，删除前的名称</summary>
    public string Name { get; }

    /// <summary>原位置，完整普通路径</summary>
    public string OriginalPath { get; }

    /// <summary>删除日期，本地时区</summary>
    public DateTime DeletedLocal { get; }

    public string DeletedText => DeletedLocal == DateTime.MinValue
        ? string.Empty
        : DeletedLocal.ToString("yyyy/M/d HH:mm");

    /// <summary>大小，单位字节</summary>
    public long SizeBytes { get; }

    /// <summary>大小显示文本，文件夹显示「N 个项目，X」</summary>
    public string SizeText { get; }

    /// <summary>类型名，如「文本文档」「文件夹」</summary>
    public string TypeName { get; }

    public bool IsDirectory { get; }

    /// <summary>小图标，16 像素</summary>
    public ImageSource? Icon { get; }

    public string SortName => Name;

    public string ToolTipText => $"{Name}\n{OriginalPath}";

    public static string FormatSize(long bytes) => DeletePlanner.FormatSize(bytes);

    /// <inheritdoc/>
    public override string ToString() => Name;
}
