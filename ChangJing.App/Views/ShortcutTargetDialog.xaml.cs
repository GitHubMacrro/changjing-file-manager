using System.IO;
using System.Windows;
using System.Windows.Input;

using IOPath = System.IO.Path;

namespace ChangJing.App.Views;

/// <summary>新建快捷方式对话框，输入目标完整路径</summary>
public partial class ShortcutTargetDialog : Wpf.Ui.Controls.FluentWindow
{
    /// <summary>创建对话框</summary>
    public ShortcutTargetDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => { PathBox.Focus(); };
    }

    /// <summary>用户确认的目标普通路径；取消时为 null</summary>
    public string? SelectedPath { get; private set; }

    /// <summary>弹出对话框；返回用户输入的路径，取消返回 null</summary>
    public static string? Ask(Window? owner)
    {
        var d = new ShortcutTargetDialog();
        if (owner is not null)
        {
            d.Owner = owner;
        }

        return d.ShowDialog() == true ? d.SelectedPath : null;
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => TryAccept();

    // 不设 IsCancel：WPF 会自动把 Esc 映射为 DialogResult
    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            TryAccept();
            e.Handled = true;
        }
    }

    private void TryAccept()
    {
        string input = PathBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(input))
        {
            ShowError("请输入目标路径。");
            return;
        }

        if (!IOPath.IsPathRooted(input))
        {
            ShowError("请输入完整的绝对路径（例如 C:\\Windows）。");
            return;
        }

        SelectedPath = input;
        DialogResult = true;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
