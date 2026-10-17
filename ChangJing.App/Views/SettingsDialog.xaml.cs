using System.Windows;
using ChangJing.App.ViewModels;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Views;

/// <summary>设置对话框，回收站容量 + 删除行为</summary>
public partial class SettingsDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly SettingsViewModel _viewModel;

    /// <summary>创建设置对话框</summary>
    public SettingsDialog(SettingsViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeComponent();
        DataContext = _viewModel;
    }

    /// <summary>用户是否点了「保存」且校验通过</summary>
    public bool Saved { get; private set; }

    /// <summary>是否存在「选项」分页</summary>
    public bool HasOptionsTab => OptionsTab is not null && SettingsTabs.Items.Contains(OptionsTab);

    /// <summary>是否存在「关于」分页</summary>
    public bool HasAboutTab => AboutTab is not null && SettingsTabs.Items.Contains(AboutTab);

    /// <summary>切到「关于」分页（自动化截图用）</summary>
    public void SelectAboutTab()
    {
        if (AboutTab is not null)
        {
            AboutTab.IsSelected = true;
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.TryApplyTo(out string error))
        {
            // 校验失败：保留对话框并记录原因
            LogManager.Warning(
                typeof(SettingsDialog),
                $"设置校验失败：{error}");
            return;
        }

        Saved = true;
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Saved = false;
        DialogResult = false;
        Close();
    }
}
