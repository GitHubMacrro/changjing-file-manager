using ChangJing.App.Core.LongPath;
using ChangJing.App.Core.Settings;
using ChangJing.App.ViewModels;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Services;

/// <summary>「每个文件夹记住视图与排序」的存取</summary>
// 键 = 规范化普通路径，比较不区分大小写，上限 500 条按 LRU 淘汰
public sealed class FolderViewStore
{
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly object _gate = new();

    /// <summary>创建存取器，与主窗口共用同一份 AppSettings</summary>
    public FolderViewStore(SettingsService settingsService, AppSettings settings)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        _settings.FolderViews ??= new List<FolderViewState>();
    }

    /// <summary>当前记录条数，供验证</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _settings.FolderViews.Count;
            }
        }
    }

    /// <summary>把普通路径规范化为存储键；不可用时返回空串</summary>
    public static string NormalizeKey(string? displayPath)
    {
        if (string.IsNullOrWhiteSpace(displayPath))
        {
            return string.Empty;
        }

        try
        {
            string normalized = PathHelper.ToDisplay(PathHelper.Normalize(displayPath!));
            return normalized.TrimEnd('\\');
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(FolderViewStore), $"规范化路径失败（{displayPath}）：{ex.Message}");
            return displayPath!.TrimEnd('\\');
        }
    }

    /// <summary>无记录时的默认视图：图片占多数 > 50% 用大图标，否则详细信息</summary>
    public static FileViewMode DefaultViewModeFor(int imageCount, int fileCount)
        => fileCount > 0 && imageCount > fileCount * AppSettings.ImageMajorityThreshold
            ? FileViewMode.LargeIcons
            : FileViewMode.Details;

    /// <summary>取某个文件夹记录的状态，无记录时返回 null</summary>
    public FolderViewState? TryGet(string displayPath)
    {
        string key = NormalizeKey(displayPath);
        if (key.Length == 0)
        {
            return null;
        }

        lock (_gate)
        {
            FolderViewState? state = Find(key);
            if (state is not null)
            {
                state.LastUsedTicks = DateTime.UtcNow.Ticks;
            }

            return state;
        }
    }

    /// <summary>记录 / 更新某个文件夹的视图状态，变化时落盘</summary>
    public void Set(
        string displayPath,
        FileViewMode viewMode,
        double iconSize,
        string sortColumn,
        bool sortDescending,
        string? groupBy = null,
        bool groupDescending = false)
    {
        string key = NormalizeKey(displayPath);
        if (key.Length == 0)
        {
            return;
        }

        bool changed;
        lock (_gate)
        {
            FolderViewState? state = Find(key);
            if (state is null)
            {
                state = new FolderViewState { Path = key };
                _settings.FolderViews.Add(state);
                changed = true;
            }
            else
            {
                changed = !string.Equals(state.ViewMode, viewMode.ToString(), StringComparison.Ordinal)
                          || Math.Abs(state.IconSize - iconSize) > 0.01
                          || !string.Equals(state.SortColumn, sortColumn, StringComparison.Ordinal)
                          || state.SortDescending != sortDescending
                          || (groupBy is not null && !string.Equals(state.GroupBy, groupBy, StringComparison.Ordinal))
                          || (groupBy is not null && state.GroupDescending != groupDescending);
            }

            state.ViewMode = viewMode.ToString();
            state.IconSize = Math.Clamp(iconSize, AppSettings.MinIconSize, AppSettings.MaxIconSize);
            state.SortColumn = sortColumn;
            state.SortDescending = sortDescending;
            if (groupBy is not null)
            {
                state.GroupBy = groupBy;
                state.GroupDescending = groupDescending;
            }

            state.LastUsedTicks = DateTime.UtcNow.Ticks;

            TrimToLimit();
        }

        if (changed)
        {
            Save();
        }
    }

    /// <summary>把内存里的最近使用时间落盘，窗口关闭时调用一次</summary>
    public void Save() => _settingsService.Save(_settings);

    // 按视图记忆列宽与排序

    /// <summary>取列宽记录，键 = 「视图前缀.列名」；无记录时返回 null</summary>
    public double? TryGetColumnWidth(string column)
        => _settings.DetailsColumnWidths.TryGetValue(column, out double width) ? width : null;

    /// <summary>记录一列的宽度，变化时才落盘</summary>
    public void SetColumnWidth(string column, double width)
    {
        if (string.IsNullOrWhiteSpace(column) || double.IsNaN(width) || width < 24)
        {
            return;
        }

        lock (_gate)
        {
            if (_settings.DetailsColumnWidths.TryGetValue(column, out double current)
                && Math.Abs(current - width) < 0.5)
            {
                return;
            }

            _settings.DetailsColumnWidths[column] = width;
        }

        Save();
    }

    /// <summary>回收站页面的排序列，RecycleBinSortColumn 枚举名</summary>
    public string RecycleBinSortColumn => _settings.RecycleBinSortColumn;

    /// <summary>回收站页面是否降序</summary>
    public bool RecycleBinSortDescending => _settings.RecycleBinSortDescending;

    /// <summary>记录回收站页面的排序状态，变化时才落盘</summary>
    public void SetRecycleBinSort(string column, bool descending)
    {
        if (string.IsNullOrWhiteSpace(column))
        {
            return;
        }

        bool changed;
        lock (_gate)
        {
            changed = !string.Equals(_settings.RecycleBinSortColumn, column, StringComparison.Ordinal)
                      || _settings.RecycleBinSortDescending != descending;

            _settings.RecycleBinSortColumn = column;
            _settings.RecycleBinSortDescending = descending;
        }

        if (changed)
        {
            Save();
        }
    }

    /// <summary>搜索结果列表的排序列，FileSortColumn 枚举名，含 Folder</summary>
    public string SearchSortColumn => _settings.SearchSortColumn;

    /// <summary>搜索结果列表是否降序</summary>
    public bool SearchSortDescending => _settings.SearchSortDescending;

    /// <summary>搜索结果的分组依据，FileGroupBy 枚举名</summary>
    public string SearchGroupBy => _settings.SearchGroupBy;

    /// <summary>搜索结果分组是否降序</summary>
    public bool SearchGroupDescending => _settings.SearchGroupDescending;

    /// <summary>记录搜索结果列表的排序状态，变化时才落盘</summary>
    public void SetSearchSort(string column, bool descending)
    {
        if (string.IsNullOrWhiteSpace(column))
        {
            return;
        }

        bool changed;
        lock (_gate)
        {
            changed = !string.Equals(_settings.SearchSortColumn, column, StringComparison.Ordinal)
                      || _settings.SearchSortDescending != descending;

            _settings.SearchSortColumn = column;
            _settings.SearchSortDescending = descending;
        }

        if (changed)
        {
            Save();
        }
    }

    /// <summary>记录搜索结果的分组状态，变化时才落盘</summary>
    public void SetSearchGroup(string groupBy, bool descending)
    {
        if (string.IsNullOrWhiteSpace(groupBy))
        {
            return;
        }

        bool changed;
        lock (_gate)
        {
            changed = !string.Equals(_settings.SearchGroupBy, groupBy, StringComparison.Ordinal)
                      || _settings.SearchGroupDescending != descending;

            _settings.SearchGroupBy = groupBy;
            _settings.SearchGroupDescending = descending;
        }

        if (changed)
        {
            Save();
        }
    }

    private FolderViewState? Find(string key)
    {
        foreach (FolderViewState state in _settings.FolderViews)
        {
            if (string.Equals(state.Path, key, StringComparison.OrdinalIgnoreCase))
            {
                return state;
            }
        }

        return null;
    }

    // 超出上限时按最近使用时间淘汰最旧的记录 LRU
    private void TrimToLimit()
    {
        if (_settings.FolderViews.Count <= AppSettings.MaxFolderViews)
        {
            return;
        }

        List<FolderViewState> kept = _settings.FolderViews
            .OrderByDescending(state => state.LastUsedTicks)
            .Take(AppSettings.MaxFolderViews)
            .ToList();

        _settings.FolderViews = kept;
        LogManager.Info(
            typeof(FolderViewStore),
            $"每文件夹视图记录超过 {AppSettings.MaxFolderViews} 条，已按最近使用淘汰。");
    }
}
