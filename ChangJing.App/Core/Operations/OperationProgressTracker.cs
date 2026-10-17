using System.Diagnostics;

namespace ChangJing.App.Core.Operations;

/// <summary>进度节流，≤10 次/秒，并算速度与剩余</summary>
// 线程安全，可能从复制回调线程调用
internal sealed class OperationProgressTracker
{
    // 100 ms = 10 次/秒
    private const int MinIntervalMs = 100;

    private readonly object _gate = new();
    private readonly Stopwatch _activeStopwatch = Stopwatch.StartNew();
    private readonly Stopwatch _sinceLastReport = Stopwatch.StartNew();
    private readonly IProgress<OperationProgress>? _reporter;
    private readonly Func<OperationState> _stateProvider;
    private readonly OperationKind _kind;

    private long _pausedMilliseconds;
    private long _pauseStartedAt = -1;
    private int _totalFiles;
    private long _totalBytes;
    private int _completedFiles;
    private long _completedBytes;
    private long _currentFileTransferred;
    private string? _currentItemName;

    public OperationProgressTracker(OperationKind kind, Func<OperationState> stateProvider, IProgress<OperationProgress>? reporter)
    {
        _kind = kind;
        _stateProvider = stateProvider;
        _reporter = reporter;
    }

    public void SetTotals(int totalFiles, long totalBytes)
    {
        lock (_gate)
        {
            _totalFiles = totalFiles;
            _totalBytes = totalBytes;
        }

        // 总量变化立刻汇报，否则一直显示 0%
        Report(force: false);
    }

    /// <summary>按状态更新暂停计时，<paramref name="force"/> 时立即汇报</summary>
    public void SetState(bool force = false)
    {
        OperationState state = _stateProvider();

        lock (_gate)
        {
            bool nowPaused = state == OperationState.Paused;
            if (nowPaused && _pauseStartedAt < 0)
            {
                _pauseStartedAt = _activeStopwatch.ElapsedMilliseconds;
            }
            else if (!nowPaused && _pauseStartedAt >= 0)
            {
                _pausedMilliseconds += _activeStopwatch.ElapsedMilliseconds - _pauseStartedAt;
                _pauseStartedAt = -1;
            }
        }

        if (force)
        {
            Report(force: true);
        }
    }

    /// <summary>开始一个条目，名字显示为当前文件</summary>
    public void BeginItem(string? itemName)
    {
        lock (_gate)
        {
            _currentItemName = itemName;
            _currentFileTransferred = 0;
        }
    }

    /// <summary>复制回调中的文件内进度</summary>
    public void ReportWithinFile(long transferredBytes)
    {
        lock (_gate)
        {
            _currentFileTransferred = transferredBytes;
        }

        Report(force: false);
    }

    /// <summary>条目处理结束，计入完成数与字节</summary>
    public void CompleteItem(long bytes)
    {
        lock (_gate)
        {
            _completedFiles++;
            _completedBytes += bytes;
            _currentFileTransferred = 0;
            _currentItemName = null;
        }

        Report(force: false);
    }

    /// <summary>条目被跳过，不计入完成字节</summary>
    public void SkipItem()
    {
        lock (_gate)
        {
            _currentFileTransferred = 0;
            _currentItemName = null;
        }
    }

    public void Flush() => Report(force: true);

    private void Report(bool force)
    {
        IProgress<OperationProgress>? reporter = _reporter;
        if (reporter is null)
        {
            return;
        }

        if (!force && _sinceLastReport.ElapsedMilliseconds < MinIntervalMs)
        {
            return;
        }

        OperationProgress snapshot;
        lock (_gate)
        {
            long paused = _pausedMilliseconds;
            if (_pauseStartedAt >= 0)
            {
                paused += _activeStopwatch.ElapsedMilliseconds - _pauseStartedAt;
            }

            long activeMs = Math.Max(1, _activeStopwatch.ElapsedMilliseconds - paused);
            long transferred = _completedBytes + _currentFileTransferred;
            double speed = transferred * 1000.0 / activeMs;

            TimeSpan? remaining = null;
            if (_totalBytes > 0 && speed > 1)
            {
                long left = _totalBytes - transferred;
                if (left <= 0)
                {
                    remaining = TimeSpan.Zero;
                }
                else
                {
                    double seconds = left / speed;
                    if (seconds < TimeSpan.MaxValue.TotalSeconds)
                    {
                        remaining = TimeSpan.FromSeconds(seconds);
                    }
                }
            }

            // 并入当前文件字节，否则大文件恒为 0%
            snapshot = new OperationProgress(
                _kind,
                _stateProvider(),
                _totalFiles,
                _totalBytes,
                _completedFiles,
                transferred,
                _currentItemName,
                speed,
                remaining);
        }

        _sinceLastReport.Restart();

        try
        {
            reporter.Report(snapshot);
        }
        catch
        {
            // 汇报失败不影响执行
        }
    }
}
