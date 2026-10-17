using ChangJing.App.Views;

namespace ChangJing.App.ViewModels;

/// <summary>删除确认对话框的用途</summary>
public enum DeleteConfirmKind
{
    /// <summary>永久删除</summary>
    Permanent,

    /// <summary>可移动盘 / 网络盘：此位置的文件将被永久删除</summary>
    ExcludedVolume,

    /// <summary>单项超过回收站容量上限</summary>
    TooLarge,

    /// <summary>回收站页面：永久删除选中的条目</summary>
    RecycleBinPermanent,

    /// <summary>回收站页面：清空回收站</summary>
    RecycleBinEmpty,
}

/// <summary>删除确认对话框的提供者：由主窗口实现，删除服务据此弹出模态确认框</summary>
public interface IDeleteConfirmation
{
    /// <summary>弹出一个删除确认对话框（默认按钮为「否」）并等待用户选择</summary>
    bool Confirm(DeleteConfirmKind kind, IReadOnlyList<DeleteConfirmItem> items, string? detail);
}
