namespace ChangJing.App.ViewModels;

using ChangJing.App.Core.Operations;

/// <summary>「需要选中某一项」的事件参数</summary>
public sealed class ItemFocusEventArgs : EventArgs
{
    public ItemFocusEventArgs(FileItemViewModel item, bool beginRename)
    {
        Item = item;
        BeginRename = beginRename;
    }

    public FileItemViewModel Item { get; }

    public bool BeginRename { get; }
}

/// <summary>「需要向用户显示一条中文消息」的事件参数</summary>
public sealed class UserMessageEventArgs : EventArgs
{
    public UserMessageEventArgs(string title, string message)
    {
        Title = title;
        Message = message;
    }

    public string Title { get; }

    public string Message { get; }
}

/// <summary>「复制到…/移动到…」目标选择请求的事件参数</summary>
public sealed class CopyMoveTargetRequestEventArgs : EventArgs
{
    public CopyMoveTargetRequestEventArgs(OperationKind kind, CopyMoveTargetViewModel viewModel)
    {
        Kind = kind;
        ViewModel = viewModel;
    }

    public OperationKind Kind { get; }

    public CopyMoveTargetViewModel ViewModel { get; }
}

/// <summary>「需要管理员权限才能操作此文件夹」请求的事件参数</summary>
public sealed class ElevationRequestEventArgs : EventArgs
{
    public ElevationRequestEventArgs(string folderDisplayPath, bool allowApplyToAll)
    {
        FolderDisplayPath = folderDisplayPath;
        AllowApplyToAll = allowApplyToAll;
    }

    /// <summary>需要权限的文件夹，普通路径</summary>
    public string FolderDisplayPath { get; }

    public bool AllowApplyToAll { get; }

    /// <summary>用户选择，界面弹框后回填</summary>
    public MainViewModel.ElevationAnswer Answer { get; set; } = MainViewModel.ElevationAnswer.Skip;
}
