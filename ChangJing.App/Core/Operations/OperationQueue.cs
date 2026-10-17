using ChangJing.App.Core.LongPath;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Core.Operations;

/// <summary>任务队列，同盘串行，异盘并行</summary>
public sealed class OperationQueue
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Task> _tails = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>排队或运行中的任务数，近似值</summary>
    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _tails.Count;
            }
        }
    }

    /// <summary>提交任务，按目标盘排队</summary>
    public Task<OperationResult> EnqueueAsync(
        OperationJob job,
        IProgress<OperationProgress>? progress = null,
        Func<ConflictInfo, Task<ConflictDecision>>? conflictResolver = null,
        Action<OperationResult>? onFinished = null)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (progress is not null)
        {
            job.ProgressReporter = progress;
        }

        if (conflictResolver is not null)
        {
            job.ConflictResolver = conflictResolver;
        }

        string queueKey = GetQueueKey(job);

        return EnqueueAsyncCore(job, queueKey, onFinished);
    }

    private async Task<OperationResult> EnqueueAsyncCore(
        OperationJob job,
        string queueKey,
        Action<OperationResult>? onFinished)
    {
        Task<OperationResult> resultTask;

        lock (_gate)
        {
            Task previous = _tails.TryGetValue(queueKey, out Task? tail) ? tail : Task.CompletedTask;

            resultTask = RunAfterAsync(previous, job);

            // 用不抛异常的延续当链尾，防异常中断
            _tails[queueKey] = resultTask.ContinueWith(
                static t => { _ = t.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        OperationResult result = await resultTask.ConfigureAwait(false);

        if (onFinished is not null)
        {
            try
            {
                onFinished(result);
            }
            catch (Exception ex)
            {
                LogManager.Warning(typeof(OperationQueue), $"任务结束回调异常：{ex.Message}");
            }
        }

        return result;
    }

    private async Task<OperationResult> RunAfterAsync(Task previous, OperationJob job)
    {
        // 前一个同盘任务结束后才能开始
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // 同盘前序任务的失败不应影响本任务
        }

        return await Task.Run(() => OperationEngine.Execute(job), CancellationToken.None).ConfigureAwait(false);
    }

    private static string GetQueueKey(OperationJob job)
    {
        // 永久删除没有目标目录，按源条目所在卷排队
        if (job.Kind == OperationKind.Delete)
        {
            return job.Sources.Count > 0 ? QueueKeyForPath(job.Sources[0].SourceDisplayPath) : "::none";
        }

        string? target = job.Sources.Count > 0 ? job.Sources[0].TargetDirectoryDisplayPath : null;
        if (string.IsNullOrEmpty(target))
        {
            return "::none";
        }

        return QueueKeyForPath(target!);
    }

    private static string QueueKeyForPath(string displayPath)
    {
        try
        {
            string key = TransferVolume.GetMountRoot(PathHelper.ToLong(displayPath));
            return key.Length > 0 ? key : "::none";
        }
        catch
        {
            return "::none";
        }
    }
}
