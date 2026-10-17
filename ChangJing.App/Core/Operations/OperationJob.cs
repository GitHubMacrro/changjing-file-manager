namespace ChangJing.App.Core.Operations;

/// <summary>任务中的一个源条目</summary>
public sealed class OperationSource
{
    internal OperationSource(string sourceDisplayPath, string targetDirectoryDisplayPath)
    {
        SourceDisplayPath = sourceDisplayPath;
        TargetDirectoryDisplayPath = targetDirectoryDisplayPath;
    }

    /// <summary>源条目普通路径</summary>
    public string SourceDisplayPath { get; }

    /// <summary>该项要放入的目标目录普通路径</summary>
    public string TargetDirectoryDisplayPath { get; }

    /// <inheritdoc/>
    public override string ToString() => $"{SourceDisplayPath} → {TargetDirectoryDisplayPath}";
}

/// <summary>复制移动任务，含状态与暂停取消控制</summary>
// 引擎输入模型，暂停用事件，取消用 CTS
public sealed class OperationJob
{
    private readonly List<OperationSource> _sources = new();
    private readonly ManualResetEventSlim _resumeEvent = new(initialState: true);
    private readonly CancellationTokenSource _cts = new();

    private int _stateValue = (int)OperationState.Scanning;

    public OperationJob(OperationKind kind)
    {
        Kind = kind;
    }

    /// <summary>状态发生变化时触发，让进度窗即时刷新</summary>
    public event EventHandler? StateChanged;

    public OperationKind Kind { get; }

    /// <summary>进度汇报目标，引擎节流 ≤10 次/秒</summary>
    public IProgress<OperationProgress>? ProgressReporter { get; set; }

    /// <summary>冲突询问回调，为空时按跳过</summary>
    public Func<ConflictInfo, Task<ConflictDecision>>? ConflictResolver { get; set; }

    public IReadOnlyList<OperationSource> Sources => _sources;

    /// <summary>引擎可选项，仅内部验证用</summary>
    public OperationOptions Options { get; } = new();

    /// <summary>调用方上下文，任务结束原样带回</summary>
    public object? UserState { get; set; }

    /// <summary>当前状态，线程安全读取</summary>
    public OperationState State => (OperationState)Volatile.Read(ref _stateValue);

    public bool IsFinished
    {
        get
        {
            OperationState state = State;
            return state is OperationState.Completed or OperationState.Canceled or OperationState.Failed;
        }
    }

    public CancellationToken CancellationToken => _cts.Token;

    /// <summary>暂停继续信号，置位即继续</summary>
    internal ManualResetEventSlim ResumeEvent => _resumeEvent;

    /// <summary>添加源条目，可指定不同目标目录</summary>
    public OperationJob AddSource(string sourceDisplayPath, string targetDirectoryDisplayPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDisplayPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectoryDisplayPath);

        _sources.Add(new OperationSource(sourceDisplayPath, targetDirectoryDisplayPath));
        return this;
    }

    /// <summary>添加永久删除源，目标记为空</summary>
    public OperationJob AddDeleteSource(string sourceDisplayPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDisplayPath);

        _sources.Add(new OperationSource(sourceDisplayPath, string.Empty));
        return this;
    }

    /// <summary>暂停，幂等</summary>
    public void Pause()
    {
        if (IsFinished)
        {
            return;
        }

        _resumeEvent.Reset();
        SetState(OperationState.Paused);
    }

    /// <summary>继续，幂等</summary>
    public void Resume()
    {
        if (IsFinished)
        {
            return;
        }

        _resumeEvent.Set();
        SetState(OperationState.Running);
    }

    /// <summary>请求取消，幂等，下个检查点退出</summary>
    public void Cancel()
    {
        if (IsFinished)
        {
            return;
        }

        // 取消先放开暂停，否则永远等不到取消
        _resumeEvent.Set();
        _cts.Cancel();
    }

    /// <summary>引擎内部切状态，终态不可覆盖</summary>
    internal void SetState(OperationState state)
    {
        while (true)
        {
            int current = Volatile.Read(ref _stateValue);
            OperationState currentState = (OperationState)current;
            if (currentState is OperationState.Completed or OperationState.Canceled or OperationState.Failed)
            {
                // 已是终态，不再改变
                return;
            }

            if (Interlocked.CompareExchange(ref _stateValue, (int)state, current) == current)
            {
                if (currentState != state)
                {
                    RaiseStateChanged();
                }

                return;
            }
        }
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // 订阅者异常不影响状态机
        }
    }

    /// <summary>引擎内部：是否暂停</summary>
    internal bool IsPaused => State == OperationState.Paused;

    /// <summary>引擎内部：等继续或取消，返回 false 即已取消</summary>
    internal bool WaitIfPaused()
    {
        while (true)
        {
            if (_cts.IsCancellationRequested)
            {
                return false;
            }

            // 暂停时最多等 50 ms 再查取消
            if (_resumeEvent.Wait(50))
            {
                return !_cts.IsCancellationRequested;
            }
        }
    }
}
