using System.Windows;
using System.Windows.Controls;
using ChangJing.App.Core.Operations;
using ChangJing.App.Services;

namespace ChangJing.App.Views;

/// <summary>错误汇总对话框，列出失败项并支持复制路径 / 重试 / 提权重开</summary>
public partial class OperationErrorsDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly OperationResult _result;

    /// <summary>创建错误汇总对话框</summary>
    public OperationErrorsDialog(OperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        InitializeComponent();

        _result = result;
        ErrorList.ItemsSource = result.Errors;

        SummaryText.Text =
            $"{result.Message}（共 {result.Errors.Count} 项未成功）";

        // 无失败项时重试按钮无意义
        RetryButton.IsEnabled = result.Errors.Count > 0;

        // 含「无权限」失败项时提供「以管理员身份重新打开」
        ElevationButton.Visibility = result.Errors.Any(error => error.IsAccessDenied)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>用户点击「重试失败项」时触发；宿主据此生成新任务</summary>
    public event EventHandler? RetryRequested;

    /// <summary>用户点击「以管理员身份重新打开」时触发，携带文件夹普通路径</summary>
    public event EventHandler<string>? ElevationRelaunchRequested;

    /// <summary>失败列表</summary>
    public IReadOnlyList<OperationError> Errors => _result.Errors;

    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        RetryRequested?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnElevationClick(object sender, RoutedEventArgs e)
    {
        OperationError? denied = _result.Errors.FirstOrDefault(error => error.IsAccessDenied);
        if (denied is null)
        {
            return;
        }

        string folder = string.IsNullOrEmpty(denied.TargetDirectoryDisplayPath)
            ? Core.LongPath.PathHelper.GetParent(denied.DisplayPath) ?? string.Empty
            : denied.TargetDirectoryDisplayPath;

        ElevationRelaunchRequested?.Invoke(this, folder);
        Close();
    }

    private void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        List<OperationError> targets = ErrorList.SelectedItems.OfType<OperationError>().ToList();
        if (targets.Count == 0)
        {
            targets = _result.Errors.ToList();
        }

        if (targets.Count == 0)
        {
            return;
        }

        string text = string.Join(Environment.NewLine, targets.Select(error => error.DisplayPath));
        if (!ClipboardService.TrySetText(text))
        {
            return;
        }
    }
}
