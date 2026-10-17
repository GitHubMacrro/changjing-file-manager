using System.Windows;
using System.Windows.Controls;
using ChangJing.App.Services;
using ChangJing.App.ViewModels;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Views;

/// <summary>「复制到…/移动到…」目标选择对话框</summary>
public partial class CopyMoveTargetDialog : Wpf.Ui.Controls.FluentWindow
{
    private DeviceChangeWatcher? _deviceWatcher;

    public CopyMoveTargetDialog(CopyMoveTargetViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        ViewModel = viewModel;
        DataContext = viewModel;

        Title = viewModel.Title;
        TargetTitleBar.Title = viewModel.Title;

        Loaded += OnDialogLoaded;
        Closed += OnDialogClosed;
    }

    private void OnDialogLoaded(object sender, RoutedEventArgs e)
    {
        // 监听驱动器热插拔，刷新磁盘列表
        try
        {
            _deviceWatcher = new DeviceChangeWatcher(Dispatcher, ViewModel.RefreshDrives);
            _deviceWatcher.Attach(this);
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(CopyMoveTargetDialog), ex);
        }
    }

    private void OnDialogClosed(object? sender, EventArgs e)
    {
        _deviceWatcher?.Dispose();
        _deviceWatcher = null;
    }

    public CopyMoveTargetViewModel ViewModel { get; }

    /// <summary>用户确认的目标目录普通路径；取消时为 null</summary>
    public string? SelectedTargetPath { get; private set; }

    private void OnOtherLocationClick(object sender, RoutedEventArgs e)
    {
        OpenPicker(ViewModel.SelectedTarget?.DisplayPath);
    }

    private void OnTargetListDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // 只在双击到某一项时处理
        if (FindAncestor<ListBoxItem>(e.OriginalSource as System.Windows.DependencyObject) is null)
        {
            return;
        }

        OpenPicker(ViewModel.SelectedTarget?.DisplayPath);
    }

    private void OpenPicker(string? startPath)
    {
        try
        {
            var picker = new FolderPickerDialog(startPath)
            {
                Owner = this,
            };

            if (picker.ShowDialog() == true && !string.IsNullOrEmpty(picker.SelectedPath))
            {
                ViewModel.ApplyOtherLocation(picker.SelectedPath!);
            }
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(CopyMoveTargetDialog), ex);
        }
    }

    private static T? FindAncestor<T>(System.Windows.DependencyObject? current)
        where T : System.Windows.DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanConfirm || ViewModel.SelectedTarget is not { } target)
        {
            return;
        }

        SelectedTargetPath = target.DisplayPath;
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
