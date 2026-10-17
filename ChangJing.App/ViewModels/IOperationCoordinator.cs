using ChangJing.App.Core.Operations;
using ChangJing.App.Services;

namespace ChangJing.App.ViewModels;

/// <summary>一次复制 / 移动任务结束的事件参数，供主视图刷新列表与状态栏</summary>
public sealed class OperationCompletedEventArgs : EventArgs
{
    public OperationCompletedEventArgs(
        OperationJob job,
        OperationResult result,
        IReadOnlyList<string> sourceDirectories,
        OperationHistoryContext? history = null)
    {
        Job = job;
        Result = result;
        SourceDirectories = sourceDirectories;
        History = history;
    }

    public OperationJob Job { get; }

    public OperationResult Result { get; }

    /// <summary>各源条目所在目录，普通路径且去重</summary>
    public IReadOnlyList<string> SourceDirectories { get; }

    public OperationHistoryContext? History { get; }
}

/// <summary>文件操作协调器：把复制 / 移动任务与进度窗、冲突对话框、错误汇总对话框串起来</summary>
public interface IOperationCoordinator
{
    bool HasActiveTasks { get; }

    /// <summary>是否已进入关闭流程，此后不再触碰界面</summary>
    bool IsShuttingDown { get; }

    /// <summary>把若干条目复制到目标目录，开始前预检，不通过时返回原因且不开始任务</summary>
    FileTransferResult Copy(
        IReadOnlyList<string> sourceDisplayPaths,
        string targetDisplayPath,
        OperationHistoryContext? history = null);

    /// <summary>把若干条目移动到目标目录，开始前预检，不通过时返回原因且不开始任务</summary>
    FileTransferResult Move(
        IReadOnlyList<string> sourceDisplayPaths,
        string targetDisplayPath,
        OperationHistoryContext? history = null);

    /// <summary>剪切粘贴：把若干条目移动到目标目录，成功后清空内部剪贴板的剪切项</summary>
    FileTransferResult MoveForPaste(
        IReadOnlyList<string> sourceDisplayPaths,
        string targetDisplayPath,
        OperationHistoryContext? history = null);

    /// <summary>永久删除若干条目，不可逆，调用方须先取得用户确认</summary>
    FileTransferResult Delete(IReadOnlyList<string> sourceDisplayPaths);

    CopyToDesktopResult CopyToDesktop(IReadOnlyList<string> sourceDisplayPaths);

    /// <summary>询问一次同名冲突界面线程弹模态对话框并等待答复</summary>
    ConflictDecision AskConflict(ConflictInfo conflict);

    /// <summary>任务结束时触发，界面线程</summary>
    event EventHandler<OperationCompletedEventArgs>? OperationCompleted;

    void CancelAll();

    /// <summary>进入关闭流程：置关闭标志、取消全部任务，之后等待任务结束再关闭窗口</summary>
    void BeginShutdown();

    /// <summary>等待所有进行中的任务结束，有超时；返回是否已全部结束</summary>
    Task<bool> WaitForIdleAsync(TimeSpan timeout);
}
