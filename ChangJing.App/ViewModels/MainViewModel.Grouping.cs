using System.ComponentModel;
using System.Windows.Data;
using ChangJing.App.Core.Grouping;
using ChangJing.App.Core.Settings;
using CommunityToolkit.Mvvm.Input;
using CustomWin.Utils.Logs;

namespace ChangJing.App.ViewModels;

/// <summary>MainViewModel 的分组部分：对齐资源管理器「分组依据」</summary>
public sealed partial class MainViewModel
{
    private FileGroupBy _groupBy = FileGroupBy.None;

    private bool _groupDescending;

    private FileGroupBy _searchGroupBy = FileGroupBy.None;

    private bool _searchGroupDescending;

    /// <summary>当前生效的分组依据</summary>
    public FileGroupBy CurrentGroupBy => IsSearchResultView ? _searchGroupBy : _groupBy;

    /// <summary>当前分组是否降序</summary>
    public bool CurrentGroupDescending => IsSearchResultView ? _searchGroupDescending : _groupDescending;

    /// <summary>当前是否启用分组</summary>
    public bool IsGrouping => CurrentGroupBy != FileGroupBy.None;

    /// <summary>当前是否按名称分组</summary>
    public bool IsGroupByName => CurrentGroupBy == FileGroupBy.Name;

    /// <summary>当前是否按修改日期分组</summary>
    public bool IsGroupByDateModified => CurrentGroupBy == FileGroupBy.DateModified;

    /// <summary>当前是否按类型分组</summary>
    public bool IsGroupByType => CurrentGroupBy == FileGroupBy.Type;

    /// <summary>当前是否按大小分组</summary>
    public bool IsGroupBySize => CurrentGroupBy == FileGroupBy.Size;

    /// <summary>当前是否按「所在文件夹」分组，仅搜索结果视图</summary>
    public bool IsGroupByFolder => CurrentGroupBy == FileGroupBy.Folder;

    /// <summary>当前是否不分组</summary>
    public bool IsGroupByNone => CurrentGroupBy == FileGroupBy.None;

    /// <summary>当前分组是否升序</summary>
    public bool IsGroupAscending => !CurrentGroupDescending;

    /// <summary>当前分组是否降序</summary>
    public bool IsGroupDescending => CurrentGroupDescending;

    /// <summary>选择分组依据</summary>
    [RelayCommand]
    private void SetGroupBy(string? groupBy)
    {
        if (!Enum.TryParse(groupBy, ignoreCase: true, out FileGroupBy parsed) || !Enum.IsDefined(parsed))
        {
            return;
        }

        // 「所在文件夹」只在搜索结果视图有意义
        if (parsed == FileGroupBy.Folder && !IsSearchResultView)
        {
            return;
        }

        if (IsSearchResultView)
        {
            _searchGroupBy = parsed;
        }
        else
        {
            _groupBy = parsed;
        }

        ApplyGrouping();
        RaiseGroupStateChanged();
        PersistFolderView();
    }

    /// <summary>分组升序</summary>
    [RelayCommand]
    private void SetGroupAscending()
    {
        if (!CurrentGroupDescending)
        {
            return;
        }

        SetGroupDirection(descending: false);
    }

    /// <summary>分组降序</summary>
    [RelayCommand]
    private void SetGroupDescending()
    {
        if (CurrentGroupDescending)
        {
            return;
        }

        SetGroupDirection(descending: true);
    }

    private void SetGroupDirection(bool descending)
    {
        if (IsSearchResultView)
        {
            _searchGroupDescending = descending;
        }
        else
        {
            _groupDescending = descending;
        }

        ApplyGrouping();
        RaiseGroupStateChanged();
        PersistFolderView();
    }

    private void ApplyGrouping()
    {
        if (ItemsView.GroupDescriptions.Count > 0)
        {
            ItemsView.GroupDescriptions.Clear();
        }

        if (CurrentGroupBy == FileGroupBy.None)
        {
            foreach (FileItemViewModel item in _items)
            {
                item.GroupKey = null;
            }

            return;
        }

        foreach (FileItemViewModel item in _items)
        {
            item.GroupKey = ComputeGroupKey(item);
        }

        ItemsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FileItemViewModel.GroupKey)));
    }

    private void PrepareGroupingForLoad()
    {
        if (ItemsView.GroupDescriptions.Count > 0)
        {
            ItemsView.GroupDescriptions.Clear();
        }

        if (CurrentGroupBy != FileGroupBy.None)
        {
            ItemsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FileItemViewModel.GroupKey)));
        }
    }

    private Core.Grouping.FileGroupKey? ComputeGroupKey(FileItemViewModel item)
    {
        if (CurrentGroupBy == FileGroupBy.None)
        {
            return null;
        }

        return FileGroupRules.GetKey(
            CurrentGroupBy,
            item.Name,
            item.IsDirectory,
            item.LastWriteTime,
            item.SizeBytes,
            item.TypeName,
            item.SearchFolderPath,
            CurrentGroupDescending,
            FileGroupRules.NowProvider());
    }

    private void ApplyGroupKeyToNewItem(FileItemViewModel item) => item.GroupKey = ComputeGroupKey(item);

    private void RaiseGroupStateChanged()
    {
        OnPropertyChanged(nameof(IsGrouping));
        OnPropertyChanged(nameof(IsGroupByName));
        OnPropertyChanged(nameof(IsGroupByDateModified));
        OnPropertyChanged(nameof(IsGroupByType));
        OnPropertyChanged(nameof(IsGroupBySize));
        OnPropertyChanged(nameof(IsGroupByFolder));
        OnPropertyChanged(nameof(IsGroupByNone));
        OnPropertyChanged(nameof(IsGroupAscending));
        OnPropertyChanged(nameof(IsGroupDescending));
    }

    private void RestoreGroupState()
    {
        if (IsSearchResultView)
        {
            _searchGroupBy = ParseGroupBy(_folderViews?.SearchGroupBy);
            _searchGroupDescending = _folderViews?.SearchGroupDescending ?? false;
            return;
        }

        if (IsThisPcView || IsRecycleBinView || _folderViews is null)
        {
            _groupBy = FileGroupBy.None;
            _groupDescending = false;
            return;
        }

        FolderViewState? state = _folderViews.TryGet(_currentLocation);
        _groupBy = ParseGroupBy(state?.GroupBy);
        _groupDescending = state?.GroupDescending ?? false;
    }

    private static FileGroupBy ParseGroupBy(string? value)
        => Enum.TryParse(value, ignoreCase: true, out FileGroupBy parsed) && Enum.IsDefined(parsed)
            ? parsed
            : FileGroupBy.None;

    /// <summary>分组信息，诊断用：组名到数量，按当前视图顺序</summary>
    public IReadOnlyList<(string Name, int Count)> DescribeGroups()
    {
        var result = new List<(string, int)>();
        foreach (object group in ItemsView.Groups)
        {
            if (group is CollectionViewGroup collectionGroup)
            {
                result.Add((collectionGroup.Name?.ToString() ?? string.Empty, collectionGroup.ItemCount));
            }
        }

        return result;
    }
}
