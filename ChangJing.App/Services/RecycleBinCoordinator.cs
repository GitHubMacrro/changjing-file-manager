using ChangJing.App.Core.LongPath;
using ChangJing.App.Core.Operations;
using ChangJing.App.Core.RecycleBin;
using ChangJing.App.Core.Settings;
using ChangJing.App.ViewModels;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Services;

/// <summary>删除 / 回收站协调器：把删除去向规划接到回收站存储、永久删除引擎与界面确认框</summary>
// 删除到回收站不弹确认；永久删除必须确认（Shift+Delete、可移动 / 网络盘、单项超容量），无确认实现时按「否」
public sealed class RecycleBinCoordinator : IRecycleBinCoordinator
{
    private readonly RecycleBinStore _store;
    private readonly DeletePlanner _planner;
    private readonly IOperationCoordinator _operations;
    private readonly IDeleteConfirmation? _confirmation;

    private readonly List<RecycleBinEntry> _lastBatch = new();

    public RecycleBinCoordinator(
        RecycleBinStore store,
        IOperationCoordinator operations,
        IDeleteConfirmation? confirmation,
        DeletePlanner? planner = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _confirmation = confirmation;
        _planner = planner ?? new DeletePlanner(_store);
    }

    /// <summary>删除规划器，供界面预览去向</summary>
    public DeletePlanner Planner => _planner;

    /// <summary>回收站存储，供回收站页面读取条目</summary>
    public RecycleBinStore Store => _store;

    /// <summary>删除时不进回收站直接永久删除，每次仍确认，即时生效</summary>
    public bool DeleteWithoutRecycleBin { get; set; }

    /// <inheritdoc/>
    public event EventHandler<RecycleBinToastEventArgs>? ToastRequested;

    /// <inheritdoc/>
    public bool CanUndo => _lastBatch.Count > 0;

    /// <inheritdoc/>
    public IReadOnlyList<RecycleBinEntry> LastBatchEntries => _lastBatch;

    /// <inheritdoc/>
    public void ForgetLastBatch() => _lastBatch.Clear();

    /// <inheritdoc/>
    public DeleteRequestResult Delete(IReadOnlyList<string> sourceDisplayPaths, bool permanent)
    {
        if (sourceDisplayPaths is null || sourceDisplayPaths.Count == 0)
        {
            return DeleteRequestResult.Rejected("没有可删除的项目。");
        }

        DeletePlan plan = _planner.Plan(sourceDisplayPaths, permanent || DeleteWithoutRecycleBin);
        if (plan.IsEmpty)
        {
            return DeleteRequestResult.Rejected("没有可删除的项目（可能已被删除或不可访问）。");
        }

        var toRecycleBin = plan.ToRecycleBin.ToList();
        var toPermanent = plan.ToPermanentDelete.ToList();
        var tooLarge = plan.TooLarge.ToList();

        bool declinedAny = false;

        // 单项超出容量上限：先问用户是否永久删除
        foreach (DeletePlanItem item in tooLarge)
        {
            if (!Confirm(DeleteConfirmKind.TooLarge, ToConfirmItems(new[] { item }), item.Reason))
            {
                declinedAny = true;
                continue;
            }

            toPermanent.Add(item);
        }

        // 可移动盘 / 网络盘导致的永久删除：提示「此位置的文件将被永久删除」
        List<DeletePlanItem> excludedVolume = toPermanent.Where(item => item.ExcludedVolume).ToList();

        if (excludedVolume.Count > 0
            && !Confirm(DeleteConfirmKind.ExcludedVolume, ToConfirmItems(excludedVolume), null))
        {
            declinedAny = true;
            toPermanent.RemoveAll(item => item.ExcludedVolume);
        }

        // 强制永久删除：Shift+Delete 或设置里的「删除时不进回收站」，后者也必须每次确认
        List<DeletePlanItem> settingPermanent = toPermanent
            .Where(item => !item.ExcludedVolume && !item.TooLarge)
            .ToList();

        if (permanent && toPermanent.Count > 0)
        {
            if (!Confirm(DeleteConfirmKind.Permanent, ToConfirmItems(toPermanent), null))
            {
                // 用户明确拒绝永久删除 → 整批取消
                return DeleteRequestResult.Canceled();
            }
        }
        else if (DeleteWithoutRecycleBin && settingPermanent.Count > 0)
        {
            if (!Confirm(
                    DeleteConfirmKind.Permanent,
                    ToConfirmItems(settingPermanent),
                    "已设置为「删除时不进回收站，直接永久删除」。"))
            {
                return DeleteRequestResult.Canceled();
            }
        }

        int recycled = 0;
        var failures = new List<string>();
        var batch = new List<RecycleBinEntry>();

        // 移入回收站（同卷改名，界面线程同步，很快）
        foreach (DeletePlanItem item in toRecycleBin)
        {
            RecycleBinMoveOutcome outcome = _store.MoveIn(item.SourceDisplayPath);
            if (outcome.Success && outcome.Entry is not null)
            {
                recycled++;
                batch.Add(outcome.Entry);
            }
            else
            {
                failures.Add($"{item.Name}：{outcome.Error}");
                LogManager.Warning(typeof(RecycleBinCoordinator), $"移入回收站失败：{item.SourceDisplayPath} — {outcome.Error}");
            }
        }

        if (batch.Count > 0)
        {
            _lastBatch.Clear();
            _lastBatch.AddRange(batch);

            // 放入后总量超限 → 自动清理最早删除的条目（MoveIn 内部已清一次，这里是幂等兜底）
            foreach (string volumeRoot in batch.Select(entry => entry.VolumeRoot).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                _store.PruneByCapacity(volumeRoot);
            }
        }

        int permanentStarted = 0;
        if (toPermanent.Count > 0)
        {
            FileTransferResult result = _operations.Delete(toPermanent.Select(item => item.SourceDisplayPath).ToList());
            if (result.Started)
            {
                permanentStarted = toPermanent.Count;
            }
            else
            {
                failures.Add(result.Message);
            }
        }

        if (recycled > 0)
        {
            string toast = recycled == 1
                ? "已将 1 个项目移到回收站"
                : $"已将 {recycled} 个项目移到回收站";
            ToastRequested?.Invoke(this, new RecycleBinToastEventArgs(toast, canUndo: true));
        }

        if (failures.Count > 0 && recycled == 0 && permanentStarted == 0)
        {
            // 用户全部拒绝时按「取消」返回（不弹错误框）
            if (declinedAny)
            {
                return DeleteRequestResult.Canceled();
            }

            return DeleteRequestResult.Rejected(string.Join("；", failures));
        }

        if (recycled == 0 && permanentStarted == 0)
        {
            // 没有任何动作（用户拒绝 / 无可用项）
            return declinedAny
                ? DeleteRequestResult.Canceled()
                : DeleteRequestResult.Rejected("没有执行任何删除。");
        }

        string message = BuildMessage(recycled, permanentStarted, failures.Count);
        return DeleteRequestResult.Ok(message, recycled, permanentStarted);
    }

    /// <inheritdoc/>
    public HistoryDeleteResult MoveToRecycleBinQuiet(IReadOnlyList<string> sourceDisplayPaths)
    {
        if (sourceDisplayPaths is null || sourceDisplayPaths.Count == 0)
        {
            return HistoryDeleteResult.Rejected("没有可移到回收站的项目。");
        }

        int moved = 0;
        var failures = new List<string>();

        foreach (string path in sourceDisplayPaths)
        {
            RecycleBinMoveOutcome outcome = _store.MoveIn(path);
            if (outcome.Success)
            {
                moved++;
            }
            else
            {
                failures.Add($"{PathHelper.GetName(path)}：{outcome.Error}");
            }
        }

        if (moved == 0)
        {
            return HistoryDeleteResult.Rejected(
                failures.Count > 0 ? string.Join("；", failures) : "没有可移到回收站的项目。");
        }

        string message = $"已将 {moved} 个项目移到回收站。";
        if (failures.Count > 0)
        {
            message += $"失败 {failures.Count} 个：{string.Join("；", failures)}";
        }

        return HistoryDeleteResult.Ok(message);
    }

    /// <inheritdoc/>
    public string UndoLastDelete()
    {
        if (_lastBatch.Count == 0)
        {
            return "没有可撤销的删除。";
        }

        var entries = _lastBatch.ToList();
        _lastBatch.Clear();

        int restored = 0;
        int skipped = 0;
        var failures = new List<string>();

        foreach (RecycleBinEntry entry in entries)
        {
            RecycleBinOutcome outcome = _store.Restore(entry, ResolveRestoreConflict);
            if (outcome.Success)
            {
                restored++;
            }
            else if (outcome.Skipped)
            {
                skipped++;
            }
            else
            {
                failures.Add($"{entry.Name}：{outcome.Error}");
            }
        }

        string message = $"已还原 {restored} 个项目";
        if (skipped > 0)
        {
            message += $"，跳过 {skipped} 个（原位置已存在同名项目）";
        }

        if (failures.Count > 0)
        {
            message += $"，失败 {failures.Count} 个：{string.Join("；", failures)}";
        }

        message += "。";
        return message;
    }

    // 复用复制 / 移动协调器的冲突对话框
    private Task<ConflictDecision> ResolveRestoreConflict(ConflictInfo conflict)
        => Task.FromResult(_operations.AskConflict(conflict));

    // 回收站页面

    /// <inheritdoc/>
    public void ApplyCapacitySettings(AppSettings settings)
    {
        if (settings is null)
        {
            return;
        }

        RecycleBinCapacity.Apply(settings, _store.Options);
        LogManager.Info(
            typeof(RecycleBinCoordinator),
            $"回收站容量设置已生效：模式 {settings.RecycleBinCapacityMode}，" +
            $"百分比 {_store.Options.CapacityPercent}，固定 {_store.Options.CapacityFixedBytes} 字节。");
    }

    /// <inheritdoc/>
    public IReadOnlyList<RecycleBinEntry> ListEntries() => _store.ListAllEntries();

    /// <inheritdoc/>
    public RecycleBinOperationResult Restore(IReadOnlyList<RecycleBinEntry> entries)
    {
        if (entries is null || entries.Count == 0)
        {
            return RecycleBinOperationResult.Failed("请选择要还原的项目。");
        }

        // 从回收站页还原后不出现撤销提示条，同时清空「刚才那批可撤销删除」（已被还原，撤销会失败）
        _lastBatch.Clear();

        int restored = 0;
        int skipped = 0;
        var failures = new List<string>();

        foreach (RecycleBinEntry entry in entries)
        {
            RecycleBinOutcome outcome = _store.Restore(entry, ResolveRestoreConflict);
            if (outcome.Success)
            {
                restored++;
            }
            else if (outcome.Skipped)
            {
                skipped++;
            }
            else
            {
                failures.Add($"{entry.Name}：{outcome.Error}");
            }
        }

        string message = $"已还原 {restored} 个项目";
        if (skipped > 0)
        {
            message += $"，跳过 {skipped} 个（原位置已存在同名项目）";
        }

        if (failures.Count > 0)
        {
            message += $"，失败 {failures.Count} 个：{string.Join("；", failures)}";
        }

        message += "。";
        return RecycleBinOperationResult.Ok(message, restoredCount: restored, failedCount: failures.Count);
    }

    /// <inheritdoc/>
    public RecycleBinOperationResult DeletePermanently(IReadOnlyList<RecycleBinEntry> entries)
    {
        if (entries is null || entries.Count == 0)
        {
            return RecycleBinOperationResult.Failed("请选择要永久删除的项目。");
        }

        // 永久删除不可逆：先弹确认框（默认按钮「否」）
        if (!Confirm(DeleteConfirmKind.RecycleBinPermanent, ToConfirmItems(entries), null))
        {
            return RecycleBinOperationResult.Canceled();
        }

        _lastBatch.Clear();

        int deleted = 0;
        var failures = new List<string>();

        foreach (RecycleBinEntry entry in entries)
        {
            RecycleBinOutcome outcome = _store.DeleteEntryPermanently(entry);
            if (outcome.Success)
            {
                deleted++;
            }
            else
            {
                failures.Add($"{entry.Name}：{outcome.Error}");
            }
        }

        string message = $"已永久删除 {deleted} 个项目";
        if (failures.Count > 0)
        {
            message += $"，失败 {failures.Count} 个：{string.Join("；", failures)}";
        }

        message += "。";
        return RecycleBinOperationResult.Ok(message, deletedCount: deleted, failedCount: failures.Count);
    }

    /// <inheritdoc/>
    public RecycleBinOperationResult Empty()
    {
        List<RecycleBinEntry> entries = _store.ListAllEntries().ToList();
        if (entries.Count == 0)
        {
            return RecycleBinOperationResult.Failed("回收站为空。");
        }

        // 确认框显示项目数与总大小
        long totalBytes = entries.Sum(entry => Math.Max(0, entry.SizeBytes));
        string detail = $"共 {entries.Count} 个项目，合计 {DeletePlanner.FormatSize(totalBytes)}。";
        if (!Confirm(DeleteConfirmKind.RecycleBinEmpty, ToConfirmItems(entries), detail))
        {
            return RecycleBinOperationResult.Canceled();
        }

        int deleted = 0;
        var failures = new List<string>();

        // 逐卷清空（按条目所在卷分组，保证与列出结果一致）
        foreach (RecycleBinEntry entry in entries)
        {
            RecycleBinOutcome outcome = _store.DeleteEntryPermanently(entry);
            if (outcome.Success)
            {
                deleted++;
            }
            else
            {
                failures.Add($"{entry.Name}：{outcome.Error}");
            }
        }

        // 兜底：清理各卷上「有文件但无元数据」的残留（Empty 也会顺带删掉有元数据的项）
        foreach (string volumeRoot in entries.Select(entry => entry.VolumeRoot).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _store.Empty(volumeRoot);
        }

        string message = $"已清空回收站（{deleted} 个项目）";
        if (failures.Count > 0)
        {
            message += $"，失败 {failures.Count} 个：{string.Join("；", failures)}";
        }

        message += "。";
        return RecycleBinOperationResult.Ok(message, deletedCount: deleted, failedCount: failures.Count);
    }

    private bool Confirm(DeleteConfirmKind kind, IReadOnlyList<DeleteConfirmItem> items, string? detail)
    {
        if (_confirmation is null)
        {
            // 没有界面提供者（如进程内无窗口验证）：安全默认「否」
            LogManager.Info(typeof(RecycleBinCoordinator), $"无确认界面，按「否」处理（{kind}）");
            return false;
        }

        return _confirmation.Confirm(kind, items, detail);
    }

    private static IReadOnlyList<DeleteConfirmItem> ToConfirmItems(IEnumerable<DeletePlanItem> items)
        => items.Select(DeleteConfirmItem.FromPlanItem).ToList();

    // 创建 / 修改时间不可得，显示「—」
    private static IReadOnlyList<DeleteConfirmItem> ToConfirmItems(IEnumerable<RecycleBinEntry> entries)
        => entries.Select(DeleteConfirmItem.FromRecycleBinEntry).ToList();

    private static string BuildMessage(int recycled, int permanent, int failureCount)
    {
        var parts = new List<string>(3);
        if (recycled > 0)
        {
            parts.Add($"已将 {recycled} 个项目移到回收站");
        }

        if (permanent > 0)
        {
            parts.Add($"正在永久删除 {permanent} 个项目");
        }

        if (failureCount > 0)
        {
            parts.Add($"{failureCount} 个项目未能删除");
        }

        return parts.Count == 0 ? "没有执行任何删除。" : string.Join("；", parts) + "。";
    }
}
