using ChangJing.App.Core.FileSystem;
using ChangJing.App.Core.LongPath;
using CustomWin.Utils.Logs;

namespace ChangJing.App.ViewModels;

/// <summary>MainViewModel 的标题栏部分</summary>
// 标题栏显示时才做末尾省略（保留「畅径」）；Window.Title 始终完整
public sealed partial class MainViewModel
{
    // 缓存位置名，避免绑定每次重算磁盘 I/O
    private string? _titleLocationName;

    /// <summary>标题里的「位置名」；无位置时为空串</summary>
    public string TitleLocationName
    {
        get
        {
            if (_titleLocationName is not null)
            {
                return _titleLocationName;
            }

            _titleLocationName = ComputeTitleLocationName();
            return _titleLocationName;
        }
    }

    /// <summary>标题栏主文字；无位置时为空串</summary>
    public string TitleMainText => TitleTextFormat.Split(TitleLocationName, IsElevated).Main;

    /// <summary>标题栏后缀「 - 畅径[（管理员）]」；无位置时为空串</summary>
    public string TitleSuffixText => TitleTextFormat.Split(TitleLocationName, IsElevated).Suffix;

    /// <summary>标题栏 ToolTip：完整正常路径，不带 \\?\</summary>
    public string TitleToolTip => BuildTitleToolTip();

    /// <summary>是否显示管理员小盾牌图标</summary>
    public bool ShowTitleShield => IsElevated;

    private void RaiseTitleChanged()
    {
        _titleLocationName = null;
        OnPropertyChanged(nameof(TitleLocationName));
        OnPropertyChanged(nameof(TitleMainText));
        OnPropertyChanged(nameof(TitleSuffixText));
        OnPropertyChanged(nameof(TitleToolTip));
        OnPropertyChanged(nameof(ShowTitleShield));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(Title));
    }

    /// <summary>仅验证用：强制刷新标题绑定</summary>
    public void RefreshTitleForTest() => RaiseTitleChanged();

    private string ComputeTitleLocationName()
    {
        if (string.IsNullOrEmpty(_currentLocation))
        {
            return string.Empty;
        }

        if (IsSearchResultView)
        {
            return TitleTextFormat.SearchLocationName(_searchKeyword);
        }

        if (IsRecycleBinView || string.Equals(_currentLocation, RecycleBinToken, StringComparison.Ordinal))
        {
            return RecycleBinDisplayName;
        }

        if (IsThisPcView || string.Equals(_currentLocation, ThisPcToken, StringComparison.Ordinal))
        {
            return ThisPcDisplayName;
        }

        try
        {
            string display = PathHelper.ToDisplay(PathHelper.ToLong(_currentLocation));

            // 盘符根显示名要与导航树一致（卷标 (C:) / 本地磁盘 (C:)）
            if (TitleTextFormat.IsDriveRoot(display))
            {
                return GetDriveRootDisplayName(display);
            }

            string name = PathHelper.GetName(display);
            return string.IsNullOrEmpty(name) ? display : name;
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(MainViewModel), $"计算标题位置名失败：{ex.Message}");
            return PathHelper.ToDisplay(_currentLocation);
        }
    }

    private static string GetDriveRootDisplayName(string driveRoot)
    {
        try
        {
            string normalized = driveRoot.EndsWith('\\') ? driveRoot : driveRoot + "\\";
            foreach (DriveItem drive in LocationProvider.GetDrives())
            {
                if (string.Equals(drive.RootPath, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return drive.DisplayName;
                }
            }
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(MainViewModel), $"取盘符显示名失败（{driveRoot}）：{ex.Message}");
        }

        return TitleTextFormat.IsDriveRoot(driveRoot)
            ? $"本地磁盘 ({char.ToUpperInvariant(driveRoot[0])}:)"
            : driveRoot;
    }

    private string BuildTitleToolTip()
    {
        if (IsThisPcView || string.Equals(_currentLocation, ThisPcToken, StringComparison.Ordinal))
        {
            return ThisPcDisplayName;
        }

        if (IsRecycleBinView || string.Equals(_currentLocation, RecycleBinToken, StringComparison.Ordinal))
        {
            return RecycleBinDisplayName;
        }

        if (IsSearchResultView)
        {
            return TitleTextFormat.SearchLocationName(_searchKeyword);
        }

        if (string.IsNullOrEmpty(_currentLocation))
        {
            return ApplicationTitle;
        }

        // 完整正常路径，永不含 \\?\
        return PathHelper.ToDisplay(_currentLocation);
    }

    /// <summary>改名后同步「当前文件夹」路径</summary>
    public bool RemapCurrentLocation(string originalDisplayPath, string newDisplayPath)
    {
        if (string.IsNullOrEmpty(originalDisplayPath) || string.IsNullOrEmpty(newDisplayPath)
            || string.IsNullOrEmpty(_currentLocation)
            || IsThisPcView || IsRecycleBinView)
        {
            return false;
        }

        string current;
        string original;
        try
        {
            current = PathHelper.ToDisplay(PathHelper.Normalize(_currentLocation));
            original = PathHelper.ToDisplay(PathHelper.Normalize(originalDisplayPath));
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(MainViewModel), $"同步当前文件夹路径失败：{ex.Message}");
            return false;
        }

        if (string.Equals(current, original, StringComparison.OrdinalIgnoreCase))
        {
            _currentLocation = PathHelper.ToDisplay(PathHelper.Normalize(newDisplayPath));
            RaiseTitleChanged();
            OnPropertyChanged(nameof(CurrentLocationDisplay));
            return true;
        }

        string prefix = original.EndsWith('\\') ? original : original + "\\";
        if (current.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            _currentLocation = PathHelper.ToDisplay(PathHelper.Normalize(newDisplayPath))
                + current.Substring(original.Length);
            RaiseTitleChanged();
            OnPropertyChanged(nameof(CurrentLocationDisplay));
            return true;
        }

        return false;
    }
}
