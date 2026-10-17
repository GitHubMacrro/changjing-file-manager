using ChangJing.App.Core.LongPath;
using ChangJing.App.Core.Operations;
using ChangJing.App.Core.PathRepair;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChangJing.App.ViewModels;

/// <summary>「修复路径」对话框列表里的一行</summary>
public sealed partial class PathRepairItemViewModel : ObservableObject
{
    private readonly PathRepairCandidate _candidate;
    private readonly Func<PathRepairItemViewModel, string, PathRepairValidation> _validator;

    public PathRepairItemViewModel(
        PathRepairCandidate candidate,
        Func<PathRepairItemViewModel, string, PathRepairValidation> validator)
    {
        _candidate = candidate;
        _validator = validator;

        Name = candidate.Name;
        DisplayPath = candidate.DisplayPath;
        ParentFolderDisplayPath = candidate.ParentFolderDisplayPath;
        IsFolder = candidate.IsDirectory;
        IssueText = candidate.IssueText;
        CurrentLength = candidate.CurrentLength;
        SuggestedName = candidate.Suggestion.Name;
        FolderShortenName = candidate.Suggestion.FolderShortenName;
        FolderShortenNote = candidate.Suggestion.FolderShortenNote ?? string.Empty;

        Validate();
    }

    public string Name { get; }

    /// <summary>原完整普通路径</summary>
    public string DisplayPath { get; }

    /// <summary>所在文件夹普通路径</summary>
    public string ParentFolderDisplayPath { get; }

    public bool IsFolder { get; }

    /// <summary>问题类型的中文描述</summary>
    public string IssueText { get; }

    public int CurrentLength { get; }

    /// <summary>「缩短所在文件夹名」建议；不需要时为 <c>null</c></summary>
    public string? FolderShortenName { get; }

    /// <summary>「缩短所在文件夹名」提示文字；不需要时为空串</summary>
    public string FolderShortenNote { get; }

    public bool CanSelect => true;

    public string ToolTipText => $"{Name}\n{DisplayPath}\n问题：{IssueText}（当前 {CurrentLength} 字符）";

    /// <summary>是否勾选，默认全选</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveName))]
    private bool _isSelected = true;

    [ObservableProperty]
    private string _suggestedName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LengthText))]
    private int _suggestedLength;

    public string LengthText => SuggestedLength.ToString();

    /// <summary>实时校验的错误；合法时为空串</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _validationError = string.Empty;

    /// <summary>是否校验失败，界面标红</summary>
    public bool HasError => ValidationError.Length > 0;

    public string EffectiveName => SuggestedName;

    public bool IsChanged => !string.Equals(SuggestedName, Name, StringComparison.Ordinal);

    partial void OnSuggestedNameChanged(string value) => Validate();

    /// <summary>重新校验建议名称并刷新修复后长度</summary>
    public void Validate()
    {
        PathRepairValidation validation = _validator(this, SuggestedName);
        ValidationError = validation.Error;
        SuggestedLength = validation.IsValid
            ? validation.Length
            : ParentFolderDisplayPath.TrimEnd('\\').Length + 1 + (SuggestedName?.Length ?? 0);

        OnPropertyChanged(nameof(IsChanged));
    }

    public PathRepairPlanItem? BuildPlanItem()
    {
        if (!IsSelected || HasError || string.IsNullOrWhiteSpace(SuggestedName))
        {
            return null;
        }

        return new PathRepairPlanItem(DisplayPath, Name, ParentFolderDisplayPath, SuggestedName);
    }
}
