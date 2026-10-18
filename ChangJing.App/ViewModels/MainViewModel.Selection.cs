using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChangJing.App.ViewModels;

/// <summary>MainViewModel 的选择部分：项目复选框、全选 / 全部取消 / 反向选择</summary>
public sealed partial class MainViewModel
{
    /// <summary>请求界面把列表选择设成给定项</summary>
    public event EventHandler<IReadOnlyList<FileItemViewModel>>? SelectionApplyRequested;

    /// <summary>项目复选框模式，随设置持久化</summary>
    [ObservableProperty]
    private bool _showCheckBoxes;

    /// <summary>筛选后的可见项数，取视图缓存不遍历</summary>
    public int VisibleItemCount
        => ItemsView is System.Windows.Data.CollectionView view ? view.Count : _items.Count;

    /// <summary>详细信息表头全选框状态：全选 true、部分选中 null、未选 false</summary>
    public bool? HeaderCheckState
    {
        get
        {
            if (_selectedItems.Count == 0)
            {
                return false;
            }

            return _selectedItems.Count >= VisibleItemCount ? true : null;
        }
    }

    [RelayCommand]
    private void ToggleCheckBoxes() => ShowCheckBoxes = !ShowCheckBoxes;

    [RelayCommand(CanExecute = nameof(CanSelectAll))]
    private void SelectAllItems() => ApplySelection(BuildVisibleItems());

    [RelayCommand(CanExecute = nameof(CanClearSelection))]
    private void ClearSelection() => ApplySelection(Array.Empty<FileItemViewModel>());

    [RelayCommand(CanExecute = nameof(CanInvertSelection))]
    private void InvertSelection()
    {
        List<FileItemViewModel> visible = BuildVisibleItems();
        var selected = new HashSet<FileItemViewModel>(_selectedItems);
        ApplySelection(visible.Where(item => !selected.Contains(item)).ToList());
    }

    public bool CanSelectAll()
        => CanOperateOnList() && VisibleItemCount > 0 && _selectedItems.Count < VisibleItemCount;

    public bool CanClearSelection() => _selectedItems.Count > 0;

    public bool CanInvertSelection() => CanOperateOnList() && VisibleItemCount > 0;

    private bool CanOperateOnList() => !IsThisPcView && !IsRecycleBinView;

    /// <summary>当前视图里可见的条目，全选 / 反选按它算</summary>
    private List<FileItemViewModel> BuildVisibleItems()
    {
        var result = new List<FileItemViewModel>(VisibleItemCount);
        foreach (object entry in ItemsView)
        {
            if (entry is FileItemViewModel item)
            {
                result.Add(item);
            }
        }

        return result;
    }

    // 界面在活动列表上落实选择；无界面时直接改选择列表
    /// <summary>把列表选择设成给定项，界面存在时同步到活动列表</summary>
    public void ApplySelection(IReadOnlyList<FileItemViewModel> items)
    {
        if (SelectionApplyRequested is { } handler)
        {
            handler(this, items);
            return;
        }

        UpdateSelection(items);
    }

    /// <summary>状态栏的选中摘要，只累计文件大小，文件夹不计</summary>
    private string BuildSelectionStatus()
    {
        ulong totalSize = 0;
        bool hasFile = false;

        foreach (FileItemViewModel item in _selectedItems)
        {
            if (!item.IsDirectory)
            {
                totalSize += item.SizeBytes;
                hasFile = true;
            }
        }

        string text = $"已选择 {_selectedItems.Count} 个项目";
        return hasFile ? $"{text}，共 {FileItemViewModel.FormatSize(totalSize)}" : text;
    }

    /// <summary>选中项变化后刷新选择相关命令与表头状态</summary>
    private void RaiseSelectionStateChanged()
    {
        SelectAllItemsCommand.NotifyCanExecuteChanged();
        ClearSelectionCommand.NotifyCanExecuteChanged();
        InvertSelectionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HeaderCheckState));
        OnPropertyChanged(nameof(VisibleItemCount));
    }
}
