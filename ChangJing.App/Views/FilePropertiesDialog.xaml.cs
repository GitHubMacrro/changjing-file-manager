using System.ComponentModel;
using System.Windows;
using ChangJing.App.ViewModels;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Views;

/// <summary>条目属性对话框，只读</summary>
public partial class FilePropertiesDialog : Wpf.Ui.Controls.FluentWindow
{
    public FilePropertiesDialog(FilePropertiesViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        ViewModel = viewModel;
        DataContext = viewModel;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    public FilePropertiesViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        if (!ViewModel.Snapshot.IsDirectory)
        {
            return;
        }

        try
        {
            await ViewModel.MeasureFolderSizeAsync();
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(FilePropertiesDialog), ex);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // 关闭时取消后台统计，避免窗口关闭后回调继续碰界面
        ViewModel.CancelSizeCalculation();
    }

    private void OnCancelSizeClick(object sender, RoutedEventArgs e)
    {
        ViewModel.CancelSizeCalculation();
    }
}
