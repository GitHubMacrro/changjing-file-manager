using System.Windows;
using ChangJing.App.ViewModels;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Views;

/// <summary>「修复路径」对话框，让资源管理器也能处理这些文件</summary>
public partial class PathRepairDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly PathRepairViewModel _viewModel;

    /// <summary>创建对话框</summary>
    public PathRepairDialog(PathRepairViewModel viewModel, Action<string>? openFolderHandler = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;
        OpenFolderHandler = openFolderHandler;

        viewModel.PickFolderRequested += OnPickFolderRequested;
        viewModel.CloseRequested += Close;
        viewModel.Completed += OnCompleted;

        Loaded += (_, _) =>
        {
            try
            {
                viewModel.BeginScan();
            }
            catch (Exception ex)
            {
                LogManager.Error(typeof(PathRepairDialog), ex);
            }
        };
    }

    /// <summary>「打开所在文件夹」的处理</summary>
    public Action<string>? OpenFolderHandler { get; set; }

    /// <summary>视图模型</summary>
    public PathRepairViewModel ViewModel => _viewModel;

    /// <summary>完成面板是否可见</summary>
    public bool IsCompletedPanelVisible => CompletedPanel.Visibility == Visibility.Visible;

    private string? OnPickFolderRequested(string startPath)
    {
        try
        {
            var picker = new FolderPickerDialog(startPath)
            {
                Owner = IsLoaded ? this : null,
            };

            return picker.ShowDialog() == true ? picker.SelectedPath : null;
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(PathRepairDialog), ex);
            return null;
        }
    }

    private void OnCompleted(object? sender, PathRepairExecutionResult result)
    {
        try
        {
            CompletedText.Text = result.Message;
            CompletedDetailText.Text = result.Moved
                ? "现在可以在资源管理器中打开该文件夹。"
                : "现在可以在资源管理器中打开这些文件。";

            // 失败项直接列在完成页，用户看不到日志
            FailureList.ItemsSource = result.Failures;
            bool hasFailures = result.Failures.Count > 0;
            FailureHeaderText.Visibility = hasFailures ? Visibility.Visible : Visibility.Collapsed;
            FailureList.Visibility = hasFailures ? Visibility.Visible : Visibility.Collapsed;

            OpenFolderButton.Visibility = result.OpenFolderDisplayPath is { Length: > 0 }
                ? Visibility.Visible
                : Visibility.Collapsed;
            OpenFolderButton.Tag = result.OpenFolderDisplayPath;

            CompletedPanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(PathRepairDialog), ex);
        }
    }

    private void OnRenameModeChecked(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.Mode = PathRepairMode.Rename;
        }
    }

    private void OnMoveModeChecked(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.Mode = PathRepairMode.Move;
        }
    }

    private void OnBrowseTargetClick(object sender, RoutedEventArgs e) => _viewModel.BrowseTarget();

    private void OnExecuteClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _viewModel.Start();
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(PathRepairDialog), ex);
            ScanStatusText.Text = $"修复时发生错误：{ex.Message}";
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (OpenFolderButton.Tag is string folder && folder.Length > 0)
        {
            try
            {
                OpenFolderHandler?.Invoke(folder);
            }
            catch (Exception ex)
            {
                LogManager.Error(typeof(PathRepairDialog), ex);
            }
        }

        Close();
    }
}
