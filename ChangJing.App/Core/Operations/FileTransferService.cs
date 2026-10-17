using ChangJing.App.Core.FileSystem;
using ChangJing.App.Core.LongPath;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Core.Operations;

/// <summary>复制移动引擎的门面，转任务交给队列</summary>
// 非阻塞：只做校验入队即返回，执行在后台
public sealed class FileTransferService : IFileTransferService
{
    private readonly OperationQueue _queue;

    /// <summary>队列为空时内部新建一个</summary>
    public FileTransferService(OperationQueue? queue = null)
    {
        _queue = queue ?? new OperationQueue();
    }

    /// <inheritdoc/>
    public bool IsImplemented => true;

    public OperationQueue Queue => _queue;

    /// <summary>默认进度汇报目标，可空</summary>
    public IProgress<OperationProgress>? ProgressReporter { get; set; }

    /// <summary>默认冲突回调，为空时按跳过处理</summary>
    public Func<ConflictInfo, Task<ConflictDecision>>? ConflictResolver { get; set; }

    /// <summary>任务结束时触发，在后台线程</summary>
    public event EventHandler<OperationResult>? OperationFinished;

    /// <inheritdoc/>
    public FileTransferResult Copy(IReadOnlyList<string> sourceDisplayPaths, string targetDisplayPath)
        => Start(OperationKind.Copy, sourceDisplayPaths, targetDisplayPath);

    /// <inheritdoc/>
    public FileTransferResult Move(IReadOnlyList<string> sourceDisplayPaths, string targetDisplayPath)
        => Start(OperationKind.Move, sourceDisplayPaths, targetDisplayPath);

    /// <summary>入队一个任务并返回其完成的 <see cref="Task"/></summary>
    public Task<OperationResult> Enqueue(OperationJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return _queue.EnqueueAsync(
            job,
            ProgressReporter,
            ConflictResolver,
            result => OperationFinished?.Invoke(this, result));
    }

    /// <summary>入队任务，可指定本次进度与冲突回调</summary>
    public Task<OperationResult> Enqueue(
        OperationJob job,
        IProgress<OperationProgress>? progress,
        Func<ConflictInfo, Task<ConflictDecision>>? conflictResolver,
        Action<OperationResult>? onFinished = null)
    {
        ArgumentNullException.ThrowIfNull(job);

        return _queue.EnqueueAsync(job, progress, conflictResolver, onFinished);
    }

    private FileTransferResult Start(
        OperationKind kind,
        IReadOnlyList<string> sourceDisplayPaths,
        string targetDisplayPath)
    {
        string verb = kind == OperationKind.Move ? "移动" : "复制";

        if (sourceDisplayPaths is null || sourceDisplayPaths.Count == 0)
        {
            return FileTransferResult.Rejected($"没有要{verb}的项目。");
        }

        if (string.IsNullOrWhiteSpace(targetDisplayPath))
        {
            return FileTransferResult.Rejected($"目标位置无效，无法{verb}。");
        }

        if (!TryGetTargetDirectory(targetDisplayPath, out string targetDisplay, out string targetError))
        {
            LogManager.Warning(typeof(FileTransferService), $"{verb}被拒绝：{targetError}（目标 {targetDisplayPath}）");
            return FileTransferResult.Rejected(targetError);
        }

        var job = new OperationJob(kind);
        foreach (string source in sourceDisplayPaths)
        {
            if (!string.IsNullOrWhiteSpace(source))
            {
                job.AddSource(source, targetDisplay);
            }
        }

        if (job.Sources.Count == 0)
        {
            return FileTransferResult.Rejected($"没有要{verb}的项目。");
        }

        // 开始前预检，不通过则不开始任务
        OperationPreflightResult preflight = OperationPreflight.Check(job);
        if (!preflight.IsValid)
        {
            LogManager.Warning(typeof(FileTransferService), $"{verb}被拒绝：{preflight.Error}");
            return FileTransferResult.Rejected(preflight.Error);
        }

        _ = Enqueue(job);

        LogManager.Info(
            typeof(FileTransferService),
            $"已受理{verb}：{job.Sources.Count} 个项目 → {targetDisplay}");

        return FileTransferResult.Accepted($"正在{verb} {job.Sources.Count} 个项目…");
    }

    private static bool TryGetTargetDirectory(string targetDisplayPath, out string targetDisplay, out string error)
    {
        targetDisplay = targetDisplayPath;
        error = string.Empty;

        string targetLong;
        try
        {
            targetLong = PathHelper.ToLong(targetDisplayPath);
            targetDisplay = PathHelper.ToDisplay(targetLong);
        }
        catch (Exception ex)
        {
            error = $"目标位置无效：{ex.Message}";
            return false;
        }

        if (!NativeMethods.GetFileAttributesExW(
                targetLong,
                NativeMethods.GetFileExInfoStandard,
                out NativeMethods.WIN32_FILE_ATTRIBUTE_DATA data))
        {
            error = "目标位置不存在或不可用。";
            return false;
        }

        if ((data.dwFileAttributes & NativeMethods.FileAttributeDirectory) == 0)
        {
            error = "目标位置不是文件夹。";
            return false;
        }

        return true;
    }
}
