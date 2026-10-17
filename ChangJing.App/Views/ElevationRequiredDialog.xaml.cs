using System.Windows;
using System.Windows.Input;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Views;

/// <summary>「需要管理员权限」对话框的用户选择</summary>
public enum ElevationChoice
{
    Relaunch,

    Skip,

    Cancel,
}

/// <summary>「需要管理员权限」对话框，提供重新打开 / 跳过 / 取消</summary>
public partial class ElevationRequiredDialog : Wpf.Ui.Controls.FluentWindow
{
    public ElevationRequiredDialog(string folderPath, bool allowApplyToAll)
    {
        InitializeComponent();

        FolderTextBlock.Text = folderPath ?? string.Empty;

        // 不允许「应用到全部」时隐藏复选框，ApplyToAll 恒为 false
        ApplyToAllCheck.Visibility = allowApplyToAll ? Visibility.Visible : Visibility.Collapsed;
        if (!allowApplyToAll)
        {
            ApplyToAllCheck.IsChecked = false;
        }

        // 默认落在「以管理员身份重新打开」上
        Loaded += (_, _) => FocusRelaunchButton();
    }

    public ElevationChoice Choice { get; private set; } = ElevationChoice.Cancel;

    public bool ApplyToAll => ApplyToAllCheck.IsChecked == true;

    /// <summary>以模态方式弹出对话框并返回用户选择</summary>
    public static ElevationChoice Ask(Window? owner, string folderPath, bool allowApplyToAll, out bool applyToAll)
    {
        var dialog = new ElevationRequiredDialog(folderPath, allowApplyToAll);

        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();

        applyToAll = dialog.ApplyToAll;
        return dialog.Choice;
    }

    private void FocusRelaunchButton()
    {
        try
        {
            RelaunchButton.Focus();
            Keyboard.Focus(RelaunchButton);
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(ElevationRequiredDialog), $"聚焦「以管理员身份重新打开」按钮失败：{ex.Message}");
        }
    }

    private void OnRelaunchClick(object sender, RoutedEventArgs e) => Complete(ElevationChoice.Relaunch, true);

    private void OnSkipClick(object sender, RoutedEventArgs e) => Complete(ElevationChoice.Skip, true);

    private void OnCancelClick(object sender, RoutedEventArgs e) => Complete(ElevationChoice.Cancel, false);

    private void Complete(ElevationChoice choice, bool dialogResult)
    {
        Choice = choice;
        DialogResult = dialogResult;
        Close();
    }
}
