using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ChangJing.App.ViewModels;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Views;

/// <summary>MainWindow 的视图模式部分：模板 / 面板 / 容器切换、选中同步、Ctrl+滚轮缩放</summary>
public partial class MainWindow
{
    private void InitializeViewModes()
    {
        _viewModel.ViewModeChanged += OnViewModeChanged;

        // 图标视图里的 Ctrl+滚轮连续缩放
        IconListView.PreviewMouseWheel += OnIconListPreviewMouseWheel;

        ApplyViewModeVisuals();
    }

    private void OnViewModeChanged(object? sender, EventArgs e) => ApplyViewModeVisuals();

    private void ApplyViewModeVisuals()
    {
        FileViewMode mode = _viewModel.ViewMode;

        DataTemplate? template = mode switch
        {
            FileViewMode.SmallIcons or FileViewMode.List => (DataTemplate)FindResource("SmallIconRowTemplate"),
            FileViewMode.Tiles => (DataTemplate)FindResource("TileItemTemplate"),
            FileViewMode.Content => (DataTemplate)FindResource("ContentItemTemplate"),
            _ => (DataTemplate)FindResource("IconTileTemplate"),
        };

        Style containerStyle = (Style)FindResource(
            mode is FileViewMode.Tiles or FileViewMode.Content or FileViewMode.SmallIcons or FileViewMode.List
                ? "CompactRowItemStyle"
                : "IconTileItemStyle");

        ItemsPanelTemplate panel = mode == FileViewMode.Content
            ? (ItemsPanelTemplate)FindResource("ContentPanelTemplate")
            : (ItemsPanelTemplate)FindResource("IconWrapPanelTemplate");

        IconListView.ItemTemplate = template;
        IconListView.ItemContainerStyle = containerStyle;

        // ItemsPanel 换了 WPF 会重建面板
        if (!ReferenceEquals(IconListView.ItemsPanel, panel))
        {
            IconListView.ItemsPanel = panel;
        }

        // 列表可见性由 XAML 绑定 ShowFileListView / ShowIconListView 决定

        SyncSelectionToActiveList();
    }

    private void SyncSelectionToActiveList()
    {
        System.Windows.Controls.ListView list = ActiveListView;
        IReadOnlyList<FileItemViewModel> selected = _viewModel.SelectedItems;

        try
        {
            list.SelectedItems.Clear();
            foreach (FileItemViewModel item in selected)
            {
                list.SelectedItems.Add(item);
            }
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(MainWindow), $"切换视图同步选中失败：{ex.Message}");
            return;
        }

        if (selected.Count > 0)
        {
            list.ScrollIntoView(selected[0]);
        }
    }

    private void OnIconListPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        e.Handled = true;
        double size = e.Delta > 0 ? _viewModel.IconSize * 1.15 : _viewModel.IconSize / 1.15;
        _viewModel.SetIconSize(size);
    }

    // 图标视图的二维方向键：左/右 ±1，上/下 ±每行列数
    private bool TryMoveSelectionInGrid(KeyEventArgs e)
    {
        if (!_viewModel.IsNonDetailsView)
        {
            return false;
        }

        int step;
        switch (e.Key)
        {
            case Key.Left:
                step = -1;
                break;
            case Key.Right:
                step = 1;
                break;
            case Key.Up:
                step = -GetItemsPerLine();
                break;
            case Key.Down:
                step = GetItemsPerLine();
                break;
            default:
                return false;
        }

        System.Windows.Controls.ListView list = ActiveListView;
        int count = list.Items.Count;
        if (count == 0)
        {
            return true;
        }

        int current = list.SelectedIndex;
        int target = current < 0 ? 0 : Math.Clamp(current + step, 0, count - 1);

        SelectAndBringIntoView((FileItemViewModel)list.Items[target]);
        _viewModel.UpdateSelection(list.SelectedItems.OfType<FileItemViewModel>());
        return true;
    }

    // 每行（内容视图为每屏）能放几个，方向键用
    private int GetItemsPerLine()
    {
        if (_viewModel.IsContentView)
        {
            return 1;
        }

        double itemWidth = Math.Max(1, _viewModel.ItemSize.Width);
        double available = Math.Max(itemWidth, IconListView.ActualWidth - 8);
        return Math.Max(1, (int)(available / itemWidth));
    }
}
