namespace ChangJing.App.Core.Operations;

/// <summary>一次复制 / 移动任务结束后的汇总结果</summary>
public sealed class OperationResult
{
    private readonly List<OperationError> _errors;

    internal OperationResult(
        OperationKind kind,
        OperationState state,
        string targetDirectoryDisplayPath,
        int succeededCount,
        int skippedCount,
        IReadOnlyList<OperationError> errors,
        string message)
    {
        Kind = kind;
        State = state;
        TargetDirectoryDisplayPath = targetDirectoryDisplayPath;
        SucceededCount = succeededCount;
        SkippedCount = skippedCount;
        _errors = new List<OperationError>(errors);
        Message = message;
    }

    public OperationKind Kind { get; }

    public OperationState State { get; }

    /// <summary>目标目录普通路径，重试用</summary>
    public string TargetDirectoryDisplayPath { get; }

    /// <summary>成功处理的顶层条目数</summary>
    public int SucceededCount { get; }

    public int SkippedCount { get; }

    /// <summary>失败项列表，不会为 null</summary>
    public IReadOnlyList<OperationError> Errors => _errors;

    public bool Success => State == OperationState.Completed && _errors.Count == 0;

    public bool Canceled => State == OperationState.Canceled;

    public string Message { get; }

    /// <summary>用失败项生成重试任务，空则 null</summary>
    public OperationJob? CreateRetryJob()
    {
        if (_errors.Count == 0)
        {
            return null;
        }

        var job = new OperationJob(Kind);
        foreach (OperationError error in _errors)
        {
            if (Kind == OperationKind.Delete)
            {
                job.AddDeleteSource(error.SourceDisplayPath);
            }
            else
            {
                job.AddSource(error.SourceDisplayPath, error.TargetDirectoryDisplayPath);
            }
        }

        return job;
    }

    /// <summary>构造一个「未开始即失败」的结果</summary>
    internal static OperationResult CreateFailed(OperationKind kind, string targetDirectoryDisplayPath, string message)
        => new(kind, OperationState.Failed, targetDirectoryDisplayPath, 0, 0, Array.Empty<OperationError>(), message);
}
