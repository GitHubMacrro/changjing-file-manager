using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace ChangJing.App.Core.Collections;

/// <summary>批量更新的集合：一批增删只发一次 Reset 通知</summary>
public sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    private int _updateDepth;
    private bool _pendingNotification;

    /// <summary>开始一批更新，期间不发变更通知</summary>
    public void BeginUpdate()
    {
        _updateDepth++;
    }

    /// <summary>结束一批更新，最外层结束时发一次 Reset</summary>
    public void EndUpdate()
    {
        if (_updateDepth == 0)
        {
            return;
        }

        _updateDepth--;
        if (_updateDepth == 0 && _pendingNotification)
        {
            _pendingNotification = false;
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    /// <summary>执行 action，无论是否异常都成对结束</summary>
    public void Update(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        BeginUpdate();
        try
        {
            action();
        }
        finally
        {
            EndUpdate();
        }
    }

    /// <inheritdoc/>
    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_updateDepth > 0)
        {
            _pendingNotification = true;
            return;
        }

        base.OnCollectionChanged(e);
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_updateDepth > 0)
        {
            // Count / Item[] 也合并进 Reset
            _pendingNotification = true;
            return;
        }

        base.OnPropertyChanged(e);
    }
}
