namespace ChangJing.App.Core.Settings;

/// <summary>用户设置的持久化模型，存于 %LOCALAPPDATA%\ChangJing\settings.json</summary>
public sealed class AppSettings
{
    /// <summary>窗口位置与大小</summary>
    public WindowPlacement Window { get; set; } = new();

    /// <summary>左侧导航区宽度，像素</summary>
    public double NavigationWidth { get; set; } = DefaultNavigationWidth;

    public bool IsPreviewPaneOpen { get; set; }

    /// <summary>预览窗格宽度，像素</summary>
    public double PreviewPaneWidth { get; set; } = DefaultPreviewPaneWidth;

    /// <summary>复制到/移动到对话框的最近使用目标，上限 MaxRecentTargets</summary>
    public List<string> RecentTargets { get; set; } = new();

    /// <summary>文件列表视图模式枚举名，坏值回退用，默认详细信息</summary>
    public string ViewMode { get; set; } = DefaultViewMode;

    /// <summary>图标边长像素，16~256，仅图标视图用</summary>
    public double IconSize { get; set; } = DefaultIconSize;

    /// <summary>详细信息紧凑模式，true 行高 22 否则 28</summary>
    public bool IsCompactMode { get; set; }

    /// <summary>项目复选框模式，默认关</summary>
    public bool ShowCheckBoxes { get; set; }

    /// <summary>每文件夹记住视图与排序，按规范化普通路径存，超限 LRU 淘汰</summary>
    public List<FolderViewState> FolderViews { get; set; } = new();

    /// <summary>详细信息视图列宽，键为视图命名空间.列名，各视图独立</summary>
    public Dictionary<string, double> DetailsColumnWidths { get; set; } = new();

    /// <summary>回收站页面排序列枚举名，默认删除日期</summary>
    public string RecycleBinSortColumn { get; set; } = DefaultRecycleBinSortColumn;

    /// <summary>回收站页面是否降序，默认 true 最新在前</summary>
    public bool RecycleBinSortDescending { get; set; } = true;

    /// <summary>搜索结果排序列，含 Folder，独立于每文件夹视图记忆</summary>
    public string SearchSortColumn { get; set; } = DefaultSortColumn;

    /// <summary>搜索结果列表是否降序，默认 false</summary>
    public bool SearchSortDescending { get; set; }

    /// <summary>搜索结果分组依据枚举名，默认不分组</summary>
    public string SearchGroupBy { get; set; } = DefaultGroupBy;

    /// <summary>搜索结果分组是否降序，默认 false</summary>
    public bool SearchGroupDescending { get; set; }

    /// <summary>默认分组依据，即不分组</summary>
    public const string DefaultGroupBy = "None";

    /// <summary>搜索框历史：最近搜索关键词最新在前，上限 MaxSearchHistory</summary>
    public List<string> SearchHistory { get; set; } = new();

    public const int MaxSearchHistory = 10;

    /// <summary>回收站容量上限，占卷容量百分比，仅百分比模式生效</summary>
    public double RecycleBinCapacityPercent { get; set; } = DefaultRecycleBinCapacityPercent;

    /// <summary>回收站容量模式：百分比或固定 GB，存字符串便于坏值回退</summary>
    public string RecycleBinCapacityMode { get; set; } = DefaultRecycleBinCapacityMode;

    /// <summary>回收站容量上限固定 GB，仅固定 GB 模式生效</summary>
    public double RecycleBinCapacityGigabytes { get; set; } = DefaultRecycleBinCapacityGigabytes;

    /// <summary>删除时不进回收站直接永久删除，设置界面勾选项即时生效</summary>
    public bool DeleteWithoutRecycleBin { get; set; }

    /// <summary>主题模式：跟随系统 / 浅色 / 深色，默认跟随系统</summary>
    public string ThemeMode { get; set; } = DefaultThemeMode;

    /// <summary>主题模式默认值：跟随系统</summary>
    public const string DefaultThemeMode = ThemeModeSystem;

    public const string ThemeModeSystem = "System";

    public const string ThemeModeLight = "Light";

    public const string ThemeModeDark = "Dark";

    /// <summary>回收站容量上限默认百分比，每卷 10%</summary>
    public const double DefaultRecycleBinCapacityPercent = 10.0;

    /// <summary>回收站容量百分比下限，0 表示不限制</summary>
    public const double MinRecycleBinCapacityPercent = 0;

    public const double MaxRecycleBinCapacityPercent = 100;

    public const string CapacityModePercent = "Percent";

    /// <summary>容量模式：固定大小，单位 GB</summary>
    public const string CapacityModeFixedGigabyte = "FixedGigabyte";

    /// <summary>容量模式的默认值，即百分比</summary>
    public const string DefaultRecycleBinCapacityMode = CapacityModePercent;

    /// <summary>固定大小模式的默认值，单位 GB</summary>
    public const double DefaultRecycleBinCapacityGigabytes = 10.0;

    /// <summary>固定大小模式的下限，单位 GB</summary>
    public const double MinRecycleBinCapacityGigabytes = 1;

    /// <summary>固定大小模式的上限，单位 GB</summary>
    public const double MaxRecycleBinCapacityGigabytes = 102400;

    /// <summary>默认视图模式，即详细信息</summary>
    public const string DefaultViewMode = "Details";

    /// <summary>默认排序列，即名称</summary>
    public const string DefaultSortColumn = "Name";

    /// <summary>回收站页面默认排序列，即删除日期</summary>
    public const string DefaultRecycleBinSortColumn = "DeletedDate";

    public const string ColumnWidthFilesPrefix = "files.";

    public const string ColumnWidthSearchPrefix = "search.";

    public const string ColumnWidthRecyclePrefix = "recycle.";

    public const int MaxFolderViews = 500;

    /// <summary>图标视图靠缩略图占多数的判定阈值，图片占比大于 50%</summary>
    public const double ImageMajorityThreshold = 0.5;

    /// <summary>默认图标边长，即大图标 96</summary>
    public const double DefaultIconSize = 96;

    public const double MinIconSize = 16;

    public const double MaxIconSize = 256;

    /// <summary>最近使用目标位置的保留上限，最多 10 个</summary>
    public const int MaxRecentTargets = 10;

    public const double DefaultNavigationWidth = 240;

    public const double DefaultPreviewPaneWidth = 320;

    public const double DefaultWindowWidth = 1100;

    public const double DefaultWindowHeight = 700;
}

/// <summary>窗口位置与大小仅记录普通状态下的边界与是否最大化</summary>
public sealed class WindowPlacement
{
    /// <summary>是否已有有效记录，false 时启动按默认居中</summary>
    public bool HasValue { get; set; }

    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; } = AppSettings.DefaultWindowWidth;

    public double Height { get; set; } = AppSettings.DefaultWindowHeight;

    public bool IsMaximized { get; set; }
}
