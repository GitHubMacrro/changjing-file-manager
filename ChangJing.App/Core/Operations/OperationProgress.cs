namespace ChangJing.App.Core.Operations;

/// <summary>一次进度快照，由引擎节流汇报</summary>
public readonly struct OperationProgress
{
    internal OperationProgress(
        OperationKind kind,
        OperationState state,
        int totalFiles,
        long totalBytes,
        int completedFiles,
        long completedBytes,
        string? currentItemName,
        double bytesPerSecond,
        TimeSpan? estimatedRemaining)
    {
        Kind = kind;
        State = state;
        TotalFiles = totalFiles;
        TotalBytes = totalBytes;
        CompletedFiles = completedFiles;
        CompletedBytes = completedBytes;
        CurrentItemName = currentItemName;
        BytesPerSecond = bytesPerSecond;
        EstimatedRemaining = estimatedRemaining;
    }

    public OperationKind Kind { get; }

    public OperationState State { get; }

    public int TotalFiles { get; }

    public long TotalBytes { get; }

    public int CompletedFiles { get; }

    /// <summary>已传输字节，含当前文件部分</summary>
    public long CompletedBytes { get; }

    /// <summary>当前条目名，无则为 null</summary>
    public string? CurrentItemName { get; }

    /// <summary>平均速度，单位字节/秒，暂停不计</summary>
    public double BytesPerSecond { get; }

    /// <summary>预计剩余时间，无法估算为 null</summary>
    public TimeSpan? EstimatedRemaining { get; }

    /// <summary>完成百分比，按字节 0~100</summary>
    public double Percent
    {
        get
        {
            if (TotalBytes > 0)
            {
                return Math.Min(100.0, CompletedBytes * 100.0 / TotalBytes);
            }

            if (TotalFiles > 0)
            {
                return Math.Min(100.0, CompletedFiles * 100.0 / TotalFiles);
            }

            return 100.0;
        }
    }
}
