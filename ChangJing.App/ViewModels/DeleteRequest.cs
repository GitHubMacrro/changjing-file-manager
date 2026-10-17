namespace ChangJing.App.ViewModels;

public enum DeleteRequestStatus
{
    Started,

    /// <summary>用户取消 / 拒绝确认</summary>
    Canceled,

    /// <summary>被拒绝，如没有可删除的条目</summary>
    Rejected,
}

public readonly struct DeleteRequestResult
{
    private DeleteRequestResult(DeleteRequestStatus status, string message, int recycleBinCount, int permanentCount)
    {
        Status = status;
        Message = message;
        RecycleBinCount = recycleBinCount;
        PermanentCount = permanentCount;
    }

    public DeleteRequestStatus Status { get; }

    /// <summary>面向用户的中文说明</summary>
    public string Message { get; }

    public int RecycleBinCount { get; }

    public int PermanentCount { get; }

    public bool Started => Status == DeleteRequestStatus.Started;

    public bool IsCanceled => Status == DeleteRequestStatus.Canceled;

    internal static DeleteRequestResult Ok(string message, int recycleBinCount, int permanentCount)
        => new(DeleteRequestStatus.Started, message, recycleBinCount, permanentCount);

    internal static DeleteRequestResult Canceled()
        => new(DeleteRequestStatus.Canceled, string.Empty, 0, 0);

    internal static DeleteRequestResult Rejected(string message)
        => new(DeleteRequestStatus.Rejected, message, 0, 0);
}

/// <summary>「已将 N 个项目移到回收站 [撤销]」提示条的事件参数</summary>
public sealed class RecycleBinToastEventArgs : EventArgs
{
    public RecycleBinToastEventArgs(string message, bool canUndo)
    {
        Message = message;
        CanUndo = canUndo;
    }

    public string Message { get; }

    public bool CanUndo { get; }
}
