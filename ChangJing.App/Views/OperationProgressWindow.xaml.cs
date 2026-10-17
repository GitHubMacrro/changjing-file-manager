using System.Windows;
using ChangJing.App.ViewModels;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Views;

/// <summary>进度窗（非模态，可收起 / 展开）逐任务显示进度与暂停 / 取消</summary>
public partial class OperationProgressWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly OperationProgressViewModel _viewModel;

    /// <summary>创建进度窗</summary>
    public OperationProgressWindow(OperationProgressViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;
    }

    /// <summary>暴露视图模型</summary>
    public OperationProgressViewModel ViewModel => _viewModel;

    /// <summary>把窗口相对主窗口居中</summary>
    public void PlaceCenteredOn(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        try
        {
            Owner = owner;
            WindowStartupLocation = owner.WindowState == WindowState.Minimized
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner;
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(OperationProgressWindow), $"设置进度窗居中失败：{ex.Message}");
            try
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            catch
            {
                // 忽略
            }
        }
    }

    // 取消按钮：数据已开始写入，需要一次明确的二次确认
    private void OnCancelTaskClick(object sender, RoutedEventArgs e)
    {
        // 窗口正在 / 已经关闭时不再弹确认框，避免在无效窗口上调用 ShowDialog
        if (!IsLoaded)
        {
            return;
        }

        if (sender is not FrameworkElement { DataContext: OperationTaskViewModel task })
        {
            return;
        }

        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = "取消操作",
            Content = $"确定要取消「{task.KindText} {task.SourceSummary}」吗？已完成的文件不会受影响。",
            Owner = this,
            PrimaryButtonText = "取消操作",
            CloseButtonText = "继续",
            IsCloseButtonEnabled = true,
        };

        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                Wpf.Ui.Controls.MessageBoxResult result =
                    await box.ShowDialogAsync(showAsDialog: true, cancellationToken: CancellationToken.None);

                if (result == Wpf.Ui.Controls.MessageBoxResult.Primary)
                {
                    task.CancelCommand.Execute(null);
                }
            }
            catch (Exception ex)
            {
                LogManager.Error(typeof(OperationProgressWindow), ex);
            }
        });
    }
}
