using ChangJing.App.Core.Operations;

namespace ChangJing.App.ViewModels;

/// <summary>发起一次复制 / 移动任务时携带的历史记录上下文，任务成功后据此压入撤销栈</summary>
public sealed class OperationHistoryContext
{
    /// <summary>创建历史上下文</summary>
    public OperationHistoryContext(OperationHistoryKind kind, string targetDirectory, IReadOnlyList<string> sourcePaths)
    {
        Kind = kind;
        TargetDirectory = targetDirectory;
        SourcePaths = sourcePaths;
    }

    /// <summary>操作类型</summary>
    public OperationHistoryKind Kind { get; }

    /// <summary>目标目录普通路径</summary>
    public string TargetDirectory { get; }

    /// <summary>源条目普通路径</summary>
    public IReadOnlyList<string> SourcePaths { get; }

    /// <summary>发起操作前的选中项路径，撤销后按它恢复选择；null 表示未记录</summary>
    public IReadOnlyList<string>? SelectionBeforeOperation { get; set; }
}
