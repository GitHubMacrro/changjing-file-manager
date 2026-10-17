namespace ChangJing.App.Core.Operations;

/// <summary>单项失败记录，用于汇总与重试</summary>
public sealed class OperationError
{
    internal OperationError(
        OperationKind kind,
        string name,
        string displayPath,
        string reason,
        int errorCode,
        string sourceDisplayPath,
        string targetDirectoryDisplayPath)
    {
        Kind = kind;
        Name = name;
        DisplayPath = displayPath;
        Reason = reason;
        ErrorCode = errorCode;
        SourceDisplayPath = sourceDisplayPath;
        TargetDirectoryDisplayPath = targetDirectoryDisplayPath;
    }

    public OperationKind Kind { get; }

    public string Name { get; }

    /// <summary>失败条目的完整普通路径</summary>
    public string DisplayPath { get; }

    public string Reason { get; }

    /// <summary>Win32 错误码，非 Win32 失败为 0 或 -1</summary>
    public int ErrorCode { get; }

    public bool IsAccessDenied => ErrorCode == Core.FileSystem.Win32ErrorMessages.ErrorAccessDenied;

    /// <summary>错误汇总「原因」列的显示文本</summary>
    public string ReasonDisplay => IsAccessDenied ? "需要管理员权限" : Reason;

    /// <summary>源条目普通路径，重试用</summary>
    public string SourceDisplayPath { get; }

    /// <summary>目标目录普通路径，重试用</summary>
    public string TargetDirectoryDisplayPath { get; }

    /// <inheritdoc/>
    public override string ToString() => $"{Name} — {Reason}";
}
