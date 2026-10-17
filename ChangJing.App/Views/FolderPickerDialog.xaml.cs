using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ChangJing.App.Controls;
using ChangJing.App.ViewModels;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Views;

/// <summary>程序自有的文件夹选择器，界面只显示普通路径，不显示 \\?\ 前缀</summary>
public partial class FolderPickerDialog : Wpf.Ui.Controls.FluentWindow
{
    /// <summary>视图模型</summary>
    public FolderPickerViewModel ViewModel { get; }

    /// <summary>创建选择器</summary>
    public FolderPickerDialog(string? startPath = null)
    {
        InitializeComponent();

        ViewModel = new FolderPickerViewModel();
        DataContext = ViewModel;

        Loaded += async (_, _) => await InitializeLocationAsync(startPath);
    }

    /// <summary>用户确认后选择的目标目录普通路径；取消时为 null</summary>
    public string? SelectedPath { get; private set; }

    private async Task InitializeLocationAsync(string? startPath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(startPath)
                && FolderPickerViewModel.IsExistingDirectory(startPath)
                && await ViewModel.LoadAsync(startPath!).ConfigureAwait(true))
            {
                PathBox.Focus();
                PathBox.SelectAll();
                return;
            }

            // 默认定位到系统盘
            string systemRoot = Environment.SystemDirectory;
            string drive = systemRoot.Length >= 3 ? systemRoot.Substring(0, 3) : "C:\\";
            await ViewModel.LoadAsync(drive).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(FolderPickerDialog), ex);
        }
    }

    private async void OnBreadcrumbClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string target } || string.IsNullOrEmpty(target))
        {
            return;
        }

        try
        {
            if (string.Equals(target, FolderPickerViewModel.ThisPcToken, StringComparison.Ordinal))
            {
                await ViewModel.LoadThisPcAsync().ConfigureAwait(true);
            }
            else
            {
                await ViewModel.LoadAsync(target).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(FolderPickerDialog), ex);
        }
    }

    private async void OnPathBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        try
        {
            await ViewModel.CommitPathAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(FolderPickerDialog), ex);
        }
    }

    private async void OnListMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 只在双击到某一项时进入
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is null)
        {
            return;
        }

        try
        {
            await ViewModel.ActivateEntryAsync(ViewModel.SelectedEntry).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(FolderPickerDialog), ex);
        }
    }

    private async void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            try
            {
                await ViewModel.ActivateEntryAsync(ViewModel.SelectedEntry).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                LogManager.Error(typeof(FolderPickerDialog), ex);
            }
        }
    }

    private async void OnGoUpClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.GoUpAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(FolderPickerDialog), ex);
        }
    }

    private async void OnNewFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.CreateFolderAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(FolderPickerDialog), ex);
        }
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        string? path = ViewModel.Confirm();
        if (path is null)
        {
            return;
        }

        SelectedPath = path;
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
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
}
