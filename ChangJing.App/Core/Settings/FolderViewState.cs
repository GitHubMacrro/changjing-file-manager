namespace ChangJing.App.Core.Settings;

/// <summary>每文件夹记住的视图状态：模式、图标尺寸、排序列与升降序</summary>
public sealed class FolderViewState
{
    /// <summary>文件夹规范化普通路径，不含结尾分隔符</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>视图模式名，对应 FileViewMode 的枚举名</summary>
    public string ViewMode { get; set; } = AppSettings.DefaultViewMode;

    /// <summary>图标边长像素，16~256</summary>
    public double IconSize { get; set; } = AppSettings.DefaultIconSize;

    /// <summary>排序列名 Name / DateModified / Type / Size</summary>
    public string SortColumn { get; set; } = AppSettings.DefaultSortColumn;

    public bool SortDescending { get; set; }

    /// <summary>分组依据名，默认不分组</summary>
    public string GroupBy { get; set; } = AppSettings.DefaultGroupBy;

    public bool GroupDescending { get; set; }

    /// <summary>最近使用时间，UTC Ticks，越小越早被 LRU 淘汰</summary>
    public long LastUsedTicks { get; set; }
}
