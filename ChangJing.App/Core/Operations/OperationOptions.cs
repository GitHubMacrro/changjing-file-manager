namespace ChangJing.App.Core.Operations;

/// <summary>复制移动任务的可选开关，界面不设</summary>
public sealed class OperationOptions
{
    /// <summary>强制跨卷移动，同卷也走「复制 + 删源」，仅供内部验证</summary>
    public bool ForceCrossVolume { get; set; }
}
