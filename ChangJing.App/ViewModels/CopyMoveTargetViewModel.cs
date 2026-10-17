using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using ChangJing.App.Core.LongPath;
using ChangJing.App.Core.Operations;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChangJing.App.ViewModels;

/// <summary>「复制到…/移动到…」对话框的视图模型，复制与移动共用</summary>
public sealed partial class CopyMoveTargetViewModel : ObservableObject
{
    private readonly IReadOnlyList<CopyMoveSourceItem> _sources;
    private readonly CopyMoveSpaceProbe _probe;

    private readonly ulong[] _sourceSizes;
    private bool _sizesReady;

    public CopyMoveTargetViewModel(
        OperationKind kind,
        IReadOnlyList<CopyMoveSourceItem> sources,
        IReadOnlyList<CopyMoveTargetItem> targets,
        CopyMoveSpaceProbe? probe = null)
    {
        Kind = kind;
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _probe = probe ?? CopyMoveSpaceProbe.Default;
        _sourceSizes = new ulong[_sources.Count];

        foreach (CopyMoveTargetItem target in targets)
        {
            Targets.Add(target);
        }

        TargetsView = new CollectionViewSource { Source = Targets }.View;
        TargetsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CopyMoveTargetItem.GroupHeader)));

        SpaceCalculation = Task.Run(MeasureSizes);
    }

    /// <summary>操作类型</summary>
    public OperationKind Kind { get; }

    public string Title => Kind == OperationKind.Move ? "移动到" : "复制到";

    public string PrimaryButtonText => Kind == OperationKind.Move ? "移动到这里" : "复制到这里";

    public string SelectedCountText => $"已选择 {_sources.Count} 个项目";

    public ObservableCollection<CopyMoveTargetItem> Targets { get; } = new();

    public ICollectionView TargetsView { get; }

    public Task SpaceCalculation { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private CopyMoveTargetItem? _selectedTarget;

    /// <summary>空间提示文字，不足时红字，为空不显示</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSpaceWarning))]
    private string _spaceHint = string.Empty;

    public bool HasSpaceWarning => SpaceHint.Length > 0;

    public bool CanConfirm => SelectedTarget is { IsAvailable: true } && SpaceHint.Length == 0;

    partial void OnSelectedTargetChanged(CopyMoveTargetItem? value) => RefreshSpaceCheck();

    /// <summary>应用「其他位置…」选择结果，已存在时直接选中</summary>
    public void ApplyOtherLocation(string displayPath)
    {
        if (string.IsNullOrWhiteSpace(displayPath))
        {
            return;
        }

        foreach (CopyMoveTargetItem existing in Targets)
        {
            if (string.Equals(existing.DisplayPath, displayPath, StringComparison.OrdinalIgnoreCase))
            {
                SelectedTarget = existing;
                return;
            }
        }

        string name = PathHelper.GetName(PathHelper.ToLong(displayPath));
        var item = new CopyMoveTargetItem(
            string.IsNullOrEmpty(name) ? displayPath : name,
            displayPath,
            CopyMoveTargets.GroupOther,
            Services.SystemIconService.GetIcon("folder", isDirectory: true),
            isAvailable: true);

        Targets.Add(item);
        SelectedTarget = item;
    }

    /// <summary>驱动器热插拔后刷新「此电脑」分组的磁盘列表</summary>
    public void RefreshDrives()
    {
        string? selectedPath = SelectedTarget?.DisplayPath;

        for (int i = Targets.Count - 1; i >= 0; i--)
        {
            if (string.Equals(Targets[i].GroupHeader, CopyMoveTargets.GroupDrives, StringComparison.Ordinal))
            {
                Targets.RemoveAt(i);
            }
        }

        foreach (CopyMoveTargetItem drive in CopyMoveTargets.BuildDrives())
        {
            Targets.Add(drive);
        }

        // 选中的目标若仍在，重新按路径定位，对象引用已变
        if (!string.IsNullOrEmpty(selectedPath))
        {
            foreach (CopyMoveTargetItem item in Targets)
            {
                if (string.Equals(item.DisplayPath, selectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    SelectedTarget = item;
                    break;
                }
            }
        }
    }

    public void RefreshSpaceCheck()
    {
        if (SelectedTarget is not { IsAvailable: true } target)
        {
            SpaceHint = string.Empty;
            OnPropertyChanged(nameof(CanConfirm));
            return;
        }

        if (!_sizesReady)
        {
            // 大小还没统计完：主按钮暂不可用
            SpaceHint = "正在计算所需空间…";
            OnPropertyChanged(nameof(CanConfirm));
            return;
        }

        bool isMove = Kind == OperationKind.Move;
        ulong needed = 0;
        for (int i = 0; i < _sources.Count; i++)
        {
            // 同卷移动 = 改名，不额外占空间
            if (isMove && _probe.IsSameVolume(_sources[i].DisplayPath, target.DisplayPath))
            {
                continue;
            }

            needed += _sourceSizes[i];
        }

        ulong free = _probe.GetFreeSpace(target.DisplayPath);

        if (needed == 0 || free == 0)
        {
            // 不需要额外空间或拿不到可用空间，不阻断，交给预检兜底
            SpaceHint = string.Empty;
        }
        else if (free < needed)
        {
            SpaceHint =
                $"空间不足：需要 {FileItemViewModel.FormatSize(needed)}，" +
                $"目标可用 {FileItemViewModel.FormatSize(free)}。";
        }
        else
        {
            SpaceHint = string.Empty;
        }

        OnPropertyChanged(nameof(CanConfirm));
    }

    private void MeasureSizes()
    {
        for (int i = 0; i < _sources.Count; i++)
        {
            _sourceSizes[i] = _probe.MeasureSourceBytes(_sources[i]);
        }

        _sizesReady = true;

        // 回到界面线程刷新提示
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            RefreshSpaceCheck();
            return;
        }

        dispatcher.InvokeAsync(RefreshSpaceCheck);
    }
}
