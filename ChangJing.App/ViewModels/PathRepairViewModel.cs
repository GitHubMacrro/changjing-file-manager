using System.Collections.ObjectModel;
using System.Windows.Threading;
using ChangJing.App.Core.FileSystem;
using ChangJing.App.Core.LongPath;
using ChangJing.App.Core.Operations;
using ChangJing.App.Core.PathRepair;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CustomWin.Utils.Logs;

namespace ChangJing.App.ViewModels;

/// <summary>「修复路径」的修复方式</summary>
public enum PathRepairMode
{
    /// <summary>重命名修复，默认</summary>
    Rename,

    /// <summary>移动到较短位置</summary>
    Move,
}

/// <summary>「修复路径」对话框的视图模型：扫描问题项、实时校验建议名、构建修复计划、驱动执行</summary>
public sealed partial class PathRepairViewModel : ObservableObject
{
    private readonly PathRepairRequestEventArgs _request;
    private readonly IPathRepairHost _host;
    private readonly Dispatcher _dispatcher;

    private CancellationTokenSource? _scanCts;
    private int _selectedCount;

    public PathRepairViewModel(
        PathRepairRequestEventArgs request,
        IPathRepairHost host,
        string defaultMoveTarget,
        Dispatcher? dispatcher = null)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;

        MoveTargetPath = defaultMoveTarget;
        IsScanning = true;
        ScanStatus = string.IsNullOrEmpty(request.ScanFolderDisplayPath)
            ? "正在分析所选项目…"
            : "正在扫描…";
    }

    /// <summary>扫描发现的条目，问题项</summary>
    public ObservableCollection<PathRepairItemViewModel> Items { get; } = new();

    [ObservableProperty]
    private bool _isScanning;

    /// <summary>扫描状态文字，进度 / 汇总 / 错误</summary>
    [ObservableProperty]
    private string _scanStatus = string.Empty;

    [ObservableProperty]
    private bool _isScanComplete;

    [ObservableProperty]
    private bool _scanCanceled;

    /// <summary>修复方式，单选</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRenameMode))]
    [NotifyPropertyChangedFor(nameof(IsMoveMode))]
    [NotifyPropertyChangedFor(nameof(CanExecute))]
    private PathRepairMode _mode = PathRepairMode.Rename;

    public bool IsRenameMode => Mode == PathRepairMode.Rename;

    public bool IsMoveMode => Mode == PathRepairMode.Move;

    /// <summary>移动方式的目标文件夹，普通路径</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExecute))]
    private string _moveTargetPath = string.Empty;

    [ObservableProperty]
    private bool _shortenParentFolders = true;

    public bool HasFolderShortenSuggestion { get; private set; }

    public string SelectedCountText => $"已选择 {_selectedCount} 项";

    public bool CanExecute
    {
        get
        {
            if (IsScanning)
            {
                return false;
            }

            if (IsMoveMode)
            {
                return _selectedCount > 0 && !string.IsNullOrWhiteSpace(MoveTargetPath);
            }

            foreach (PathRepairItemViewModel item in Items)
            {
                if (item.IsSelected && !item.HasError && item.IsChanged)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>请求打开文件夹选择器，参数为当前目标文件夹</summary>
    public event Func<string, string?>? PickFolderRequested;

    /// <summary>请求关闭对话框，界面处理</summary>
    public event Action? CloseRequested;

    /// <summary>修复完成，界面切换到完成面板</summary>
    public event EventHandler<PathRepairExecutionResult>? Completed;

    public void Start()
    {
        if (!CanExecute)
        {
            return;
        }

        PathRepairItemViewModel[] selected = Items.Where(item => item.IsSelected).ToArray();

        if (IsMoveMode)
        {
            List<string> sources = selected.Select(item => item.DisplayPath).ToList();
            PathRepairExecutionResult moved = _host.ExecuteRepairMove(sources, MoveTargetPath);
            if (moved.Started)
            {
                Completed?.Invoke(this, moved);
            }
            else
            {
                ScanStatus = moved.Message;
            }

            return;
        }

        var plan = new PathRepairPlan();
        var folderRenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (PathRepairItemViewModel item in selected)
        {
            if (item.HasError || string.IsNullOrWhiteSpace(item.SuggestedName))
            {
                continue;
            }

            PathRepairPlanItem? planItem = item.BuildPlanItem();
            if (planItem is null)
            {
                continue;
            }

            plan.Items.Add(planItem);

            // 「缩短所在文件夹名」一键选项，仅对给出该建议的条目生效
            if (ShortenParentFolders
                && item.FolderShortenName is { Length: > 0 } shortenedFolder
                && !string.Equals(shortenedFolder, PathHelper.GetName(item.ParentFolderDisplayPath), StringComparison.Ordinal)
                && folderRenames.Add(item.ParentFolderDisplayPath))
            {
                plan.FolderRenames.Add(new PathRepairFolderRename(item.ParentFolderDisplayPath, shortenedFolder));
            }
        }

        PathRepairExecutionResult result = _host.ExecuteRepairRenames(plan);
        Completed?.Invoke(this, result);
    }

    /// <summary>开始扫描，对话框 Loaded 时调用</summary>
    public void BeginScan()
    {
        if (_request.SelectedDisplayPaths.Count > 0)
        {
            LoadSelectedItems();
            return;
        }

        StartFolderScan();
    }

    [RelayCommand]
    private void CancelScan()
    {
        _scanCts?.Cancel();
    }

    public void SetAllSelected(bool selected)
    {
        foreach (PathRepairItemViewModel item in Items)
        {
            item.IsSelected = selected;
        }
    }

    public void BrowseTarget()
    {
        string? picked = PickFolderRequested?.Invoke(MoveTargetPath);
        if (!string.IsNullOrEmpty(picked))
        {
            MoveTargetPath = picked!;
        }
    }

    public void RequestClose() => CloseRequested?.Invoke();

    private void LoadSelectedItems()
    {
        try
        {
            IReadOnlyList<PathRepairCandidate> candidates = PathRepairScanner.AnalyzePaths(_request.SelectedDisplayPaths);
            ApplyCandidates(candidates);
            IsScanning = false;
            IsScanComplete = true;
            ScanStatus = candidates.Count == 0
                ? "所选项目没有需要修复的路径问题。"
                : $"发现 {candidates.Count} 个需要修复的项目。";
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(PathRepairViewModel), ex);
            IsScanning = false;
            IsScanComplete = true;
            ScanStatus = "分析所选项目时发生错误，详细信息已写入日志。";
        }
    }

    private void StartFolderScan()
    {
        _scanCts = new CancellationTokenSource();
        CancellationToken token = _scanCts.Token;

        Task.Run(() =>
        {
            PathRepairScanResult result = PathRepairScanner.ScanRecursive(
                _request.ScanFolderDisplayPath,
                batch => _dispatcher.InvokeAsync(() => ApplyCandidates(batch)),
                progress => _dispatcher.InvokeAsync(() =>
                    ScanStatus = $"正在扫描… 已检查 {progress.Scanned} 项，发现 {progress.Matched} 个问题"),
                token);

            _dispatcher.InvokeAsync(() => FinishScan(result));
        });
    }

    private void FinishScan(PathRepairScanResult result)
    {
        IsScanning = false;
        IsScanComplete = true;
        ScanCanceled = result.Canceled;

        if (result.Error is { Length: > 0 } && result.ProblemCount == 0)
        {
            ScanStatus = $"无法扫描该文件夹：{result.Error}";
            return;
        }

        var parts = new List<string>(3);
        parts.Add(result.Canceled
            ? $"扫描已取消，已发现 {result.ProblemCount} 个需要修复的项目。"
            : result.ProblemCount == 0
                ? "没有发现需要修复的路径问题。"
                : $"发现 {result.ProblemCount} 个需要修复的项目。");

        if (result.SkippedDirectoryCount > 0)
        {
            parts.Add($"跳过 {result.SkippedDirectoryCount} 个无法访问的文件夹");
        }

        if (result.SkippedReparsePointCount > 0)
        {
            parts.Add($"跳过 {result.SkippedReparsePointCount} 个链接");
        }

        ScanStatus = string.Join(" | ", parts);
        OnPropertyChanged(nameof(CanExecute));
    }

    private void ApplyCandidates(IReadOnlyList<PathRepairCandidate> candidates)
    {
        foreach (PathRepairCandidate candidate in candidates)
        {
            var item = new PathRepairItemViewModel(candidate, ValidateItem);
            item.PropertyChanged += OnItemPropertyChanged;
            Items.Add(item);

            if (candidate.Suggestion.FolderShortenName is { Length: > 0 })
            {
                HasFolderShortenSuggestion = true;
            }
        }

        _selectedCount = Items.Count(item => item.IsSelected);
        OnPropertyChanged(nameof(SelectedCountText));
        OnPropertyChanged(nameof(HasFolderShortenSuggestion));
        OnPropertyChanged(nameof(CanExecute));
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PathRepairItemViewModel.IsSelected) or nameof(PathRepairItemViewModel.ValidationError))
        {
            _selectedCount = Items.Count(item => item.IsSelected);
            OnPropertyChanged(nameof(SelectedCountText));
            OnPropertyChanged(nameof(CanExecute));
        }
    }

    /// <summary>实时校验回调：非法字符 / 重名 / 仍超长</summary>
    private PathRepairValidation ValidateItem(PathRepairItemViewModel item, string name)
    {
        string parent = item.ParentFolderDisplayPath;

        return PathRepairRules.Validate(name, parent, candidate =>
        {
            // 与原名相同不算重名，例如仅缩短上级文件夹
            if (string.Equals(candidate, item.Name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // 计划内其它条目（同目录）使用了同一个建议名
            foreach (PathRepairItemViewModel other in Items)
            {
                if (ReferenceEquals(other, item))
                {
                    continue;
                }

                if (string.Equals(other.ParentFolderDisplayPath, parent, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(other.SuggestedName, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // 磁盘兜底
            try
            {
                string longParent = PathHelper.ToLong(parent);
                return FileOperations.Exists(PathHelper.Combine(longParent, candidate));
            }
            catch
            {
                return false;
            }
        });
    }
}
