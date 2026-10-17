using System.Windows;
using System.Windows.Threading;
using ChangJing.App.Core.FileSystem;
using ChangJing.App.Core.LongPath;
using ChangJing.App.Core.Operations;
using ChangJing.App.ViewModels;
using ChangJing.App.Views;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Services;

/// <summary>文件操作协调器：把复制 / 移动任务与进度窗、冲突对话框、错误汇总对话框串起来</summary>
// 视图模型只调 IOperationCoordinator，本类是唯一把「引擎 + 窗口」粘合的地方，跨线程回调都切回界面线程
public sealed class OperationCoordinator : IOperationCoordinator, ICopyToDesktopService
{
    private readonly FileTransferService _service;
    private readonly Dispatcher _dispatcher;
    private readonly AppClipboard _clipboard;
    private readonly OperationProgressViewModel _progress = new();

    private readonly object _idleGate = new();
    private TaskCompletionSource<bool>? _idleTcs;

    private OperationProgressWindow? _window;
    private int _activeCount;
    private int _shuttingDown;

    public OperationCoordinator(FileTransferService service, Dispatcher dispatcher, AppClipboard clipboard)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
    }

    /// <summary>供验证读取进度视图模型</summary>
    public OperationProgressViewModel Progress => _progress;

    /// <inheritdoc/>
    public event EventHandler<OperationCompletedEventArgs>? OperationCompleted;

    /// <inheritdoc/>
    public bool HasActiveTasks => Volatile.Read(ref _activeCount) > 0;

    /// <inheritdoc/>
    public bool IsShuttingDown => Volatile.Read(ref _shuttingDown) != 0;

    /// <inheritdoc/>
    public FileTransferResult Copy(
        IReadOnlyList<string> sourceDisplayPaths,
        string targetDisplayPath,
        OperationHistoryContext? history = null)
        => Start(OperationKind.Copy, sourceDisplayPaths, targetDisplayPath, clearCutOnSuccess: false, history);

    /// <inheritdoc/>
    public FileTransferResult Move(
        IReadOnlyList<string> sourceDisplayPaths,
        string targetDisplayPath,
        OperationHistoryContext? history = null)
        => Start(OperationKind.Move, sourceDisplayPaths, targetDisplayPath, clearCutOnSuccess: false, history);

    /// <summary>剪切粘贴：成功后清空内部剪贴板的剪切项</summary>
    public FileTransferResult MoveForPaste(
        IReadOnlyList<string> sourceDisplayPaths,
        string targetDisplayPath,
        OperationHistoryContext? history = null)
        => Start(OperationKind.Move, sourceDisplayPaths, targetDisplayPath, clearCutOnSuccess: true, history);

    /// <inheritdoc/>
    public FileTransferResult Delete(IReadOnlyList<string> sourceDisplayPaths)
        => StartDelete(sourceDisplayPaths);

    /// <inheritdoc/>
    public bool IsImplemented => true;

    /// <inheritdoc/>
    public CopyToDesktopResult CopyToDesktop(IReadOnlyList<string> sourceDisplayPaths)
    {
        if (sourceDisplayPaths is null || sourceDisplayPaths.Count == 0)
        {
            return CopyToDesktopResult.Failed("没有要复制的项目。");
        }

        string? desktop = GetDesktopPath();
        if (desktop is null)
        {
            return CopyToDesktopResult.Failed("无法定位桌面文件夹。");
        }

        FileTransferResult result = Start(OperationKind.Copy, sourceDisplayPaths, desktop, clearCutOnSuccess: false);
        return result.Started
            ? CopyToDesktopResult.Ok("正在复制到桌面…")
            : CopyToDesktopResult.Failed(result.Message);
    }

    /// <inheritdoc/>
    public void CancelAll()
    {
        foreach (OperationTaskViewModel task in _progress.Tasks.ToList())
        {
            task.CancelCommand.Execute(null);
        }
    }

    /// <inheritdoc/>
    public void BeginShutdown()
    {
        Volatile.Write(ref _shuttingDown, 1);
        CancelAll();

        if (!HasActiveTasks)
        {
            SignalIdleIfNeeded();
        }
    }

    /// <inheritdoc/>
    public async Task<bool> WaitForIdleAsync(TimeSpan timeout)
    {
        if (!HasActiveTasks)
        {
            return true;
        }

        TaskCompletionSource<bool> tcs;
        lock (_idleGate)
        {
            _idleTcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            tcs = _idleTcs;
        }

        Task completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(false);
        return completed == tcs.Task;
    }

    private void SignalIdleIfNeeded()
    {
        TaskCompletionSource<bool>? tcs;
        lock (_idleGate)
        {
            tcs = _idleTcs;
            _idleTcs = null;
        }

        tcs?.TrySetResult(true);
    }

    // 启动

    private FileTransferResult Start(
        OperationKind kind,
        IReadOnlyList<string> sourceDisplayPaths,
        string targetDisplayPath,
        bool clearCutOnSuccess,
        OperationHistoryContext? history = null)
    {
        string verb = kind == OperationKind.Move ? "移动" : "复制";

        if (IsShuttingDown)
        {
            return FileTransferResult.Rejected("程序正在退出，无法开始新的操作。");
        }

        if (sourceDisplayPaths is null || sourceDisplayPaths.Count == 0)
        {
            return FileTransferResult.Rejected($"没有要{verb}的项目。");
        }

        if (string.IsNullOrWhiteSpace(targetDisplayPath))
        {
            return FileTransferResult.Rejected($"目标位置无效，无法{verb}。");
        }

        var job = new OperationJob(kind);
        foreach (string source in sourceDisplayPaths)
        {
            if (!string.IsNullOrWhiteSpace(source))
            {
                job.AddSource(source, targetDisplayPath);
            }
        }

        if (job.Sources.Count == 0)
        {
            return FileTransferResult.Rejected($"没有要{verb}的项目。");
        }

        // 开始前预检：不通过则直接返回原因（不建任务行、不弹进度窗）
        OperationPreflightResult preflight = OperationPreflight.Check(job);
        if (!preflight.IsValid)
        {
            LogManager.Warning(typeof(OperationCoordinator), $"{verb}被拒绝：{preflight.Error}");
            return FileTransferResult.Rejected(preflight.Error);
        }

        string sourceSummary = BuildSourceSummary(job);

        // 每个任务单独一行进度：进度目标必须在界面线程构造，保证回调自动回到界面线程
        var task = new OperationTaskViewModel(
            job,
            sourceSummary,
            PathHelper.ToDisplay(PathHelper.ToLong(targetDisplayPath)),
            OnTaskCloseRequested);

        var progress = new Progress<OperationProgress>(task.ApplyProgress);

        task.ViewErrorsRequested += (_, result) => ShowErrors(result);

        Interlocked.Increment(ref _activeCount);

        _progress.Add(task);
        _progress.NotifyTitleChanged();
        ShowWindow();

        IReadOnlyList<string> sourceDirectories = CollectSourceDirectories(job);

        _ = _service.Enqueue(
            job,
            progress,
            conflict => ResolveConflict(conflict),
            result => OnTaskFinished(task, job, result, sourceDirectories, clearCutOnSuccess, history));

        LogManager.Info(
            typeof(OperationCoordinator),
            $"已开始{verb}：{job.Sources.Count} 个项目 → {targetDisplayPath}");

        return FileTransferResult.Accepted($"正在{verb} {job.Sources.Count} 个项目…");
    }

    private static string BuildSourceSummary(OperationJob job)
    {
        if (job.Sources.Count == 1)
        {
            return job.Sources[0].SourceDisplayPath;
        }

        string first = PathHelper.GetName(PathHelper.ToLong(job.Sources[0].SourceDisplayPath));
        return $"{first} 等 {job.Sources.Count} 项";
    }

    // 永久删除

    // 走同一套进度窗 / 错误汇总（调用方须先取得用户确认）
    private FileTransferResult StartDelete(IReadOnlyList<string> sourceDisplayPaths)
    {
        if (IsShuttingDown)
        {
            return FileTransferResult.Rejected("程序正在退出，无法开始新的操作。");
        }

        if (sourceDisplayPaths is null || sourceDisplayPaths.Count == 0)
        {
            return FileTransferResult.Rejected("没有可删除的项目。");
        }

        var job = new OperationJob(OperationKind.Delete);
        foreach (string source in sourceDisplayPaths)
        {
            if (!string.IsNullOrWhiteSpace(source))
            {
                job.AddDeleteSource(source);
            }
        }

        if (job.Sources.Count == 0)
        {
            return FileTransferResult.Rejected("没有可删除的项目。");
        }

        OperationPreflightResult preflight = OperationPreflight.Check(job);
        if (!preflight.IsValid)
        {
            LogManager.Warning(typeof(OperationCoordinator), $"删除被拒绝：{preflight.Error}");
            return FileTransferResult.Rejected(preflight.Error);
        }

        string sourceSummary = BuildSourceSummary(job);
        var task = new OperationTaskViewModel(job, sourceSummary, string.Empty, OnTaskCloseRequested);
        var progress = new Progress<OperationProgress>(task.ApplyProgress);
        task.ViewErrorsRequested += (_, result) => ShowErrors(result);

        Interlocked.Increment(ref _activeCount);
        _progress.Add(task);
        _progress.NotifyTitleChanged();
        ShowWindow();

        IReadOnlyList<string> sourceDirectories = CollectSourceDirectories(job);

        _ = _service.Enqueue(
            job,
            progress,
            conflict => ResolveConflict(conflict),
            result => OnTaskFinished(task, job, result, sourceDirectories, clearCutOnSuccess: false));

        LogManager.Info(typeof(OperationCoordinator), $"已开始永久删除：{job.Sources.Count} 个项目");

        return FileTransferResult.Accepted($"正在永久删除 {job.Sources.Count} 个项目…");
    }

    private static IReadOnlyList<string> CollectSourceDirectories(OperationJob job)
    {
        var directories = new List<string>();
        foreach (OperationSource source in job.Sources)
        {
            string? parent = PathHelper.GetParent(source.SourceDisplayPath);
            if (!string.IsNullOrEmpty(parent)
                && !directories.Contains(parent, StringComparer.OrdinalIgnoreCase))
            {
                directories.Add(parent);
            }
        }

        return directories;
    }

    // 结束

    private void OnTaskFinished(
        OperationTaskViewModel task,
        OperationJob job,
        OperationResult result,
        IReadOnlyList<string> sourceDirectories,
        bool clearCutOnSuccess,
        OperationHistoryContext? history = null)
    {
        // 引擎在后台线程回调，统一切回界面线程；关闭流程中只更新状态，不触碰界面
        _dispatcher.InvokeAsync(() =>
        {
            Interlocked.Decrement(ref _activeCount);

            if (IsShuttingDown)
            {
                // 关闭流程：只更新状态（不弹窗、不创建窗口），并等待引擎收尾
                task.ApplyFinished(result);
                SignalIdleIfNeeded();

                if (!HasActiveTasks)
                {
                    CloseWindow();
                }

                return;
            }

            task.ApplyFinished(result);

            // 保留结束的行，让用户能看到结果与原因；全部成功且窗口仍开着时 3 秒后自动移除
            if (result.Success)
            {
                ScheduleAutoRemove(task);
            }
            else
            {
                _progress.NotifyTitleChanged();
            }

            if (clearCutOnSuccess && result.Success)
            {
                _clipboard.ClearCut();
            }

            OperationCompleted?.Invoke(this, new OperationCompletedEventArgs(job, result, sourceDirectories, history));

            if (_progress.IsEmpty && !HasActiveTasks)
            {
                CloseWindow();
            }

            if (!HasActiveTasks)
            {
                SignalIdleIfNeeded();
            }
        });
    }

    // 成功任务行延迟移除（保留片刻让用户看到结果）
    private void ScheduleAutoRemove(OperationTaskViewModel task)
    {
        _progress.NotifyTitleChanged();

        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(3),
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (IsShuttingDown)
            {
                return;
            }

            _progress.Remove(task);
            _progress.NotifyTitleChanged();

            if (_progress.IsEmpty && !HasActiveTasks)
            {
                CloseWindow();
            }
        };

        timer.Start();
    }

    // 冲突

    private Task<ConflictDecision> ResolveConflict(ConflictInfo conflict)
    {
        // 关闭流程中不再弹对话框（窗口可能已 / 正在关闭），按安全的「跳过」处理
        if (IsShuttingDown)
        {
            return Task.FromResult(ConflictDecision.Skip());
        }

        // 已经在界面线程（如回收站页面的还原走界面线程同步调用）：直接弹，避免 InvokeAsync + 同步等待死锁
        if (_dispatcher.CheckAccess())
        {
            return Task.FromResult(ShowConflictDialog(conflict));
        }

        // 引擎在工作线程同步等待本 Task；这里把对话框弹到界面线程（模态于进度窗）
        return _dispatcher.InvokeAsync(() => ShowConflictDialog(conflict)).Task;
    }

    private ConflictDecision ShowConflictDialog(ConflictInfo conflict)
    {
        var dialog = new ConflictDialog(conflict)
        {
            Owner = _window is { IsLoaded: true } ? _window : Application.Current?.MainWindow,
        };

        try
        {
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(OperationCoordinator), ex);
            return ConflictDecision.Skip();
        }

        LogManager.Info(
            typeof(OperationCoordinator),
            $"冲突决定：{conflict.SourceName} → {dialog.Decision.Action}" +
            (dialog.Decision.ApplyToAll ? "（对其余冲突执行）" : string.Empty));

        return dialog.Decision;
    }

    /// <inheritdoc/>
    public ConflictDecision AskConflict(ConflictInfo conflict)
    {
        // 关闭流程 / 界面未就绪：安全默认「跳过」
        if (IsShuttingDown || Application.Current is null)
        {
            return ConflictDecision.Skip();
        }

        try
        {
            return _dispatcher.CheckAccess()
                ? ShowConflictDialog(conflict)
                : _dispatcher.Invoke(() => ShowConflictDialog(conflict));
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(OperationCoordinator), ex);
            return ConflictDecision.Skip();
        }
    }

    // 错误汇总

    private void ShowErrors(OperationResult result)
    {
        if (IsShuttingDown)
        {
            return;
        }

        var dialog = new OperationErrorsDialog(result)
        {
            Owner = _window is { IsLoaded: true } ? _window : Application.Current?.MainWindow,
        };

        dialog.RetryRequested += (_, _) => Retry(result);

        // 错误汇总里的「以管理员身份重新打开」→ 用 runas 启动自身并定位到该文件夹（当前进程不退出）
        dialog.ElevationRelaunchRequested += (_, folder) => RelaunchElevated(folder);

        dialog.ShowDialog();
    }

    // 以管理员身份重新打开（错误汇总按钮）；用户取消 UAC 时静默返回
    private static void RelaunchElevated(string folderDisplayPath)
    {
        try
        {
            if (!ElevationService.TryRelaunchElevated(folderDisplayPath, out string error) && !string.IsNullOrEmpty(error))
            {
                LogManager.Warning(typeof(OperationCoordinator), $"以管理员身份重新打开失败：{error}");
            }
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(OperationCoordinator), ex);
        }
    }

    private void Retry(OperationResult result)
    {
        if (IsShuttingDown)
        {
            return;
        }

        OperationJob? retryJob = result.CreateRetryJob();
        if (retryJob is null)
        {
            return;
        }

        string sourceSummary = BuildSourceSummary(retryJob);
        var task = new OperationTaskViewModel(
            retryJob,
            sourceSummary,
            result.TargetDirectoryDisplayPath,
            OnTaskCloseRequested);

        var progress = new Progress<OperationProgress>(task.ApplyProgress);
        task.ViewErrorsRequested += (_, r) => ShowErrors(r);

        Interlocked.Increment(ref _activeCount);
        _progress.Add(task);
        _progress.NotifyTitleChanged();
        ShowWindow();

        IReadOnlyList<string> sourceDirectories = CollectSourceDirectories(retryJob);

        _ = _service.Enqueue(
            retryJob,
            progress,
            conflict => ResolveConflict(conflict),
            r => OnTaskFinished(task, retryJob, r, sourceDirectories, clearCutOnSuccess: false));
    }

    // 窗口生命周期

    private void ShowWindow()
    {
        // 关闭流程中不再显示进度窗
        if (IsShuttingDown)
        {
            return;
        }

        if (_window is null)
        {
            _window = new OperationProgressWindow(_progress);

            Window? owner = Application.Current?.MainWindow;
            if (owner is not null && owner.IsLoaded && owner.IsVisible && owner.WindowState != WindowState.Minimized)
            {
                _window.PlaceCenteredOn(owner);
            }
            else
            {
                _window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }

        if (!_window.IsVisible)
        {
            _window.Show();
        }

        _window.Activate();
    }

    private void OnTaskCloseRequested(OperationTaskViewModel task)
    {
        _progress.Remove(task);
        _progress.NotifyTitleChanged();

        if (_progress.IsEmpty && !HasActiveTasks)
        {
            CloseWindow();
        }
    }

    private void CloseWindow()
    {
        OperationProgressWindow? window = _window;
        _window = null;

        if (window is null)
        {
            return;
        }

        try
        {
            if (window.IsVisible)
            {
                window.Close();
            }
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(OperationCoordinator), $"关闭进度窗失败：{ex.Message}");
        }
    }

    // 桌面路径

    private static string? GetDesktopPath()
    {
        foreach (KnownFolderItem folder in LocationProvider.GetKnownFolders())
        {
            if (folder.Id == "Desktop" && folder.IsAvailable)
            {
                return folder.DisplayPath;
            }
        }

        return null;
    }
}
