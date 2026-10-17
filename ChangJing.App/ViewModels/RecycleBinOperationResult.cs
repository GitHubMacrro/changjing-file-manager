namespace ChangJing.App.ViewModels;

/// <summary>回收站页面一次操作的结果：还原、永久删除或清空</summary>
public readonly struct RecycleBinOperationResult
{
    private RecycleBinOperationResult(
        bool isCanceled,
        bool changed,
        string message,
        int restoredCount,
        int deletedCount,
        int failedCount)
    {
        IsCanceled = isCanceled;
        Changed = changed;
        Message = message;
        RestoredCount = restoredCount;
        DeletedCount = deletedCount;
        FailedCount = failedCount;
    }

    /// <summary>用户取消：不弹错误框、无需刷新</summary>
    public bool IsCanceled { get; }

    public bool Changed { get; }

    /// <summary>面向用户的中文说明，写状态栏</summary>
    public string Message { get; }

    public int RestoredCount { get; }

    public int DeletedCount { get; }

    public int FailedCount { get; }

    /// <summary>成功完成，可能部分失败，原因见 Message</summary>
    public static RecycleBinOperationResult Ok(
        string message,
        int restoredCount = 0,
        int deletedCount = 0,
        int failedCount = 0)
        => new(false, restoredCount + deletedCount > 0, message, restoredCount, deletedCount, failedCount);

    /// <summary>用户取消，不视为改动</summary>
    public static RecycleBinOperationResult Canceled() => new(true, false, string.Empty, 0, 0, 0);

    /// <summary>被拒绝或前置失败，未改动</summary>
    public static RecycleBinOperationResult Failed(string message) => new(false, false, message, 0, 0, 0);
}
