using System.Windows;
using ChangJing.App.Core.Operations;
using ChangJing.App.Services;

namespace ChangJing.App.Views;

/// <summary>同名冲突对话框，提供「替换 / 跳过 / 保留两者」</summary>
public partial class ConflictDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly ConflictInfo _conflict;

    private ConflictAction _result = ConflictAction.Skip;

    public ConflictDialog(ConflictInfo conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);

        InitializeComponent();

        _conflict = conflict;

        string title = $"目标已包含名为 {conflict.SourceName} 的文件";
        Title = title;
        PromptText.Text = $"{title}，请选择如何处理。";

        TargetIcon.Source = SystemIconService.GetIcon(conflict.TargetName, conflict.TargetIsDirectory);
        TargetName.Text = conflict.TargetName;
        TargetSize.Text = conflict.TargetIsDirectory
            ? "类型：文件夹"
            : $"大小：{ViewModels.FileItemViewModel.FormatSize(conflict.TargetSize)}";
        TargetModified.Text = $"修改时间：{FormatTime(conflict.TargetLastWriteTime)}";

        SourceIcon.Source = SystemIconService.GetIcon(conflict.SourceName, conflict.SourceIsDirectory);
        SourceName.Text = conflict.SourceName;
        SourceSize.Text = conflict.SourceIsDirectory
            ? "类型：文件夹"
            : $"大小：{ViewModels.FileItemViewModel.FormatSize(conflict.SourceSize)}";
        SourceModified.Text = $"修改时间：{FormatTime(conflict.SourceLastWriteTime)}";

        string? keepBothName = conflict.SuggestedKeepBothName;
        KeepBothHint.Text = keepBothName is null
            ? "「保留两者」将自动重命名后放入。"
            : $"「保留两者」将把源重命名为：{keepBothName}";

        // 文件与文件夹同名时不能替换：Win32 不支持
        ReplaceButton.IsEnabled = conflict.CanReplace;
        if (!conflict.CanReplace)
        {
            ReplaceButton.ToolTip = "文件与文件夹同名，无法替换。";
        }

        ApplyToAllCheck.Content = conflict.RemainingConflicts is { } remaining
            ? $"对剩余 {remaining} 个冲突执行此操作"
            : "对其余冲突执行此操作";
    }

    public ConflictDecision Decision { get; private set; } = ConflictDecision.Skip();

    public bool ApplyToAll => ApplyToAllCheck.IsChecked == true;

    private void OnReplaceClick(object sender, RoutedEventArgs e) => Complete(ConflictAction.Replace);

    private void OnSkipClick(object sender, RoutedEventArgs e) => Complete(ConflictAction.Skip);

    private void OnKeepBothClick(object sender, RoutedEventArgs e) => Complete(ConflictAction.KeepBoth);

    private void Complete(ConflictAction action)
    {
        _result = action;
        Decision = new ConflictDecision(action, ApplyToAll);
        DialogResult = true;
        Close();
    }

    private static string FormatTime(DateTime value)
        => value == DateTime.MinValue ? "（未知）" : value.ToString("yyyy/M/d HH:mm:ss");

    public ConflictAction SelectedAction => _result;
}
