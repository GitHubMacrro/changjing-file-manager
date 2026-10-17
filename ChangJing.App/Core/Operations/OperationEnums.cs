namespace ChangJing.App.Core.Operations;

public enum OperationKind
{
    Copy,

    Move,

    /// <summary>永久删除不可逆，界面须先弹确认</summary>
    Delete,
}

public enum OperationState
{
    Scanning,

    Running,

    Paused,

    /// <summary>已完成，可能有单项失败</summary>
    Completed,

    Canceled,

    /// <summary>未开始执行就失败</summary>
    Failed,
}
