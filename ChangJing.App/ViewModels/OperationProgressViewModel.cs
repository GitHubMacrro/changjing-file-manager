using System.Collections.ObjectModel;
using ChangJing.App.Core.Operations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChangJing.App.ViewModels;

/// <summary>进度窗的视图模型：多个任务上下排列，可收起 / 展开</summary>
public sealed partial class OperationProgressViewModel : ObservableObject
{
    public ObservableCollection<OperationTaskViewModel> Tasks { get; } = new();

    public bool IsEmpty => Tasks.Count == 0;

    [ObservableProperty]
    private bool _isExpanded = true;

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    public string ExpandButtonText => IsExpanded ? "收起" : "展开";

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandButtonText));

    public void Add(OperationTaskViewModel task)
    {
        Tasks.Add(task);
        OnPropertyChanged(nameof(IsEmpty));
    }

    public void Remove(OperationTaskViewModel task)
    {
        Tasks.Remove(task);
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>任务数量，标题用</summary>
    public int Count => Tasks.Count;

    public string TitleText
    {
        get
        {
            if (Tasks.Count == 0)
            {
                return "文件操作";
            }

            string verb = Tasks.Any(t => t.KindText == "删除")
                ? "删除"
                : Tasks.Any(t => t.KindText == "移动")
                    ? "移动"
                    : "复制";

            return Tasks.Count == 1 ? $"正在{verb}" : $"正在{verb}（{Tasks.Count} 个任务）";
        }
    }

    public void NotifyTitleChanged() => OnPropertyChanged(nameof(TitleText));
}
