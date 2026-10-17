namespace ChangJing.App.Core.FileSystem;

/// <summary>搜索暂停闸门：暂停期间搜索线程阻塞等待，不丢结果</summary>
public sealed class SearchPauseToken : IDisposable
{
    private readonly ManualResetEventSlim _resume = new(true);
    private volatile bool _paused;

    public bool IsPaused => _paused;

    public void Pause()
    {
        _paused = true;
        _resume.Reset();
    }

    public void Resume()
    {
        _paused = false;
        _resume.Set();
    }

    /// <summary>若处于暂停则阻塞，直到继续或取消；取消时不抛异常</summary>
    public void WaitIfPaused(CancellationToken cancellationToken)
    {
        while (_paused && !cancellationToken.IsCancellationRequested)
        {
            _resume.Wait(100);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _resume.Dispose();
}
