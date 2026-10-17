using ChangJing.App.Core.Operations;
using ChangJing.App.Core.RecycleBin;
using ChangJing.App.Core.Settings;

namespace ChangJing.App.ViewModels;

/// <summary>删除 / 回收站的界面协调器：视图模型只调用它，由主窗口负责确认对话框、提示条与撤销</summary>
public interface IRecycleBinCoordinator
{
    /// <summary>请求删除若干条目，permanent 为 true 时强制永久删除</summary>
    DeleteRequestResult Delete(IReadOnlyList<string> sourceDisplayPaths, bool permanent);

    /// <summary>撤销刚才那一批「移到回收站」的条目，还原到原位置</summary>
    string UndoLastDelete();

    /// <summary>当前是否有一批可撤销的「移到回收站」条目</summary>
    bool CanUndo { get; }

    /// <summary>最近一批「移到回收站」的条目</summary>
    IReadOnlyList<RecycleBinEntry> LastBatchEntries { get; }

    /// <summary>清除「最近一批可撤销删除」的记忆</summary>
    void ForgetLastBatch();

    /// <summary>静默把若干条目移入回收站：不发提示条、不影响「刚才那批可撤销删除」</summary>
    HistoryDeleteResult MoveToRecycleBinQuiet(IReadOnlyList<string> sourceDisplayPaths);

    /// <summary>请求显示底部提示条</summary>
    event EventHandler<RecycleBinToastEventArgs>? ToastRequested;

    /// <summary>「删除时不进回收站，直接永久删除」设置项</summary>
    bool DeleteWithoutRecycleBin { get; set; }

    /// <summary>把容量设置应用到回收站存储，即时生效：百分比 / 固定 GB 二选一</summary>
    void ApplyCapacitySettings(AppSettings settings);

    /// <summary>列出所有卷上的回收站条目，按删除时间倒序，可在后台线程调用</summary>
    IReadOnlyList<RecycleBinEntry> ListEntries();

    /// <summary>还原若干条目到原位置：原文件夹不存在则重建；同名走现有冲突对话框</summary>
    RecycleBinOperationResult Restore(IReadOnlyList<RecycleBinEntry> entries);

    /// <summary>永久删除若干条目，不可逆；界面前置确认，默认按钮「否」</summary>
    RecycleBinOperationResult DeletePermanently(IReadOnlyList<RecycleBinEntry> entries);

    /// <summary>清空回收站，界面前置确认</summary>
    RecycleBinOperationResult Empty();
}
