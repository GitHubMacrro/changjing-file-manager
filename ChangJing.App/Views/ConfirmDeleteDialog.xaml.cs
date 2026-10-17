using System.Windows;
using System.Windows.Input;
using ChangJing.App.ViewModels;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Views;

/// <summary>删除确认对话框，默认按钮为「否」</summary>
public partial class ConfirmDeleteDialog : Wpf.Ui.Controls.FluentWindow
{
    public ConfirmDeleteDialog(DeleteConfirmKind kind, IReadOnlyList<DeleteConfirmItem> items, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(items);

        InitializeComponent();

        Items = items;

        (string title, string prompt, string yesText, string noText, string warning) = Describe(kind, items.Count);
        Title = title;
        DialogTitleBar.Title = title;
        PromptText.Text = detail is null ? prompt : $"{prompt}\n{detail}";
        WarningText.Text = warning;
        YesButton.Content = yesText;
        NoButton.Content = noText;

        PopulateDetails(kind, items);

        // 焦点默认落在「否」，避免误确认
        Loaded += (_, _) => FocusNoButton();
    }

    public IReadOnlyList<DeleteConfirmItem> Items { get; }

    public bool Confirmed { get; private set; }

    private void PopulateDetails(DeleteConfirmKind kind, IReadOnlyList<DeleteConfirmItem> items)
    {
        if (items.Count == 0)
        {
            DetailBox.Visibility = Visibility.Collapsed;
            return;
        }

        // 清空回收站即使只有 1 项也按多项展示
        bool single = items.Count == 1 && kind != DeleteConfirmKind.RecycleBinEmpty;

        if (single)
        {
            DeleteConfirmItem item = items[0];
            ItemIcon.Source = item.Icon;
            ItemNameText.Text = item.Name;
            ItemDetailText.Text = item.DetailLine;
            SingleItemPanel.Visibility = Visibility.Visible;
            MultiItemText.Visibility = Visibility.Collapsed;
            return;
        }

        long totalBytes = items.Sum(item => Math.Max(0, item.SizeBytes));
        MultiItemText.Text = $"{items.Count} 个项目，合计 {DeleteConfirmItem.FormatSize(totalBytes)}";
        MultiItemText.Visibility = Visibility.Visible;
        SingleItemPanel.Visibility = Visibility.Collapsed;
    }

    private static (string Title, string Prompt, string YesText, string NoText, string Warning) Describe(
        DeleteConfirmKind kind,
        int count)
    {
        string what = count == 1 ? "此项目" : $"这 {count} 个项目";

        return kind switch
        {
            DeleteConfirmKind.ExcludedVolume => (
                "确认永久删除",
                $"此位置的文件将被永久删除（无法从回收站还原）。是否继续删除{what}？",
                "永久删除",
                "否",
                "永久删除后无法撤销，文件不会进入回收站。"),

            DeleteConfirmKind.TooLarge => (
                "项目太大，无法放入回收站",
                $"此项目太大，无法放入回收站，是否永久删除{what}？",
                "永久删除",
                "否",
                "永久删除后无法撤销，文件不会进入回收站。"),

            DeleteConfirmKind.RecycleBinPermanent => (
                "确认永久删除",
                $"确定要从回收站永久删除{what}吗？",
                "永久删除",
                "否",
                "此操作无法撤销。"),

            DeleteConfirmKind.RecycleBinEmpty => (
                "确认清空回收站",
                $"确定要清空回收站吗？其中的{what}将被永久删除。",
                "清空回收站",
                "否",
                "清空后无法恢复。"),

            _ => (
                "确认永久删除",
                $"确定要永久删除{what}吗？",
                "永久删除",
                "否",
                "永久删除后无法撤销，文件不会进入回收站。"),
        };
    }

    private void FocusNoButton()
    {
        try
        {
            NoButton.Focus();
            Keyboard.Focus(NoButton);
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(ConfirmDeleteDialog), $"聚焦「否」按钮失败：{ex.Message}");
        }
    }

    private void OnYesClick(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
        Close();
    }

    private void OnNoClick(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
        Close();
    }
}
