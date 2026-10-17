using System.IO;
using ChangJing.App.Core.FileSystem;
using ChangJing.App.Core.LongPath;
using ChangJing.App.Core.Operations;
using ChangJing.App.Core.RecycleBin;
using ChangJing.App.Services;
using CommunityToolkit.Mvvm.Input;
using CustomWin.Utils.Logs;

namespace ChangJing.App.ViewModels;

/// <summary>MainViewModel 的通用撤销 / 重做部分：操作历史栈、命令与右键菜单文案</summary>
public sealed partial class MainViewModel
{
    private readonly OperationHistory _history = new();

    /// <summary>操作历史栈</summary>
    public OperationHistory History => _history;

    /// <summary>撤销菜单文案；无可撤销时为空串</summary>
    public string UndoMenuText => _history.CanUndo ? $"撤销 {_history.UndoLabel}" : string.Empty;

    /// <summary>重做菜单文案；无可重做时为空串</summary>
    public string RedoMenuText => _history.CanRedo ? $"重做 {_history.RedoLabel}" : string.Empty;

    /// <summary>是否有可撤销的操作</summary>
    public bool CanUndoOperation() => _history.CanUndo;

    /// <summary>是否有可重做的操作</summary>
    public bool CanRedoOperation() => _history.CanRedo;

    /// <summary>撤销最近一步</summary>
    [RelayCommand(CanExecute = nameof(CanUndoOperation))]
    private void Undo() => ExecuteHistoryStep(undo: true);

    /// <summary>重做最近一步</summary>
    [RelayCommand(CanExecute = nameof(CanRedoOperation))]
    private void Redo() => ExecuteHistoryStep(undo: false);

    private void ExecuteHistoryStep(bool undo)
    {
        HistoryStepResult result = undo
            ? _history.Undo(new HistoryOperationsBridge(this))
            : _history.Redo(new HistoryOperationsBridge(this));

        // 刷新会重写状态栏，因此结果提示用「一次性提示」追加在刷新之后
        if (!string.IsNullOrEmpty(result.Message))
        {
            _pendingStatusHint = result.Message;
        }

        if (!IsThisPcView && !IsRecycleBinView)
        {
            Refresh();
        }
        else
        {
            UpdateStatusText();
        }

        RaiseHistoryChanged();
    }

    private void RaiseHistoryChanged()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();

        // 底部「撤销」提示条按钮与通用撤销同源
        UndoDeleteCommand.NotifyCanExecuteChanged();

        OnPropertyChanged(nameof(UndoMenuText));
        OnPropertyChanged(nameof(RedoMenuText));
    }

    // 压入历史栈

    private void PushRenameHistory(string originalDisplayPath, string newDisplayPath, DateTime lastWriteUtc)
    {
        if (string.IsNullOrEmpty(originalDisplayPath) || string.IsNullOrEmpty(newDisplayPath)
            || string.Equals(originalDisplayPath, newDisplayPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _history.Push(new RenameHistoryEntry(originalDisplayPath, newDisplayPath, lastWriteUtc));
        RaiseHistoryChanged();
    }

    private void PushCreateHistory(string createdDisplayPath, bool isDirectory)
    {
        if (string.IsNullOrEmpty(createdDisplayPath))
        {
            return;
        }

        DateTime lastWriteUtc = DateTime.MinValue;
        byte[]? content = null;

        try
        {
            string longPath = PathHelper.ToLong(createdDisplayPath);
            if (FileOperations.TryGetEntryInfo(longPath, out _, out _, out DateTime utc))
            {
                lastWriteUtc = utc;
            }

            if (!isDirectory && File.Exists(longPath))
            {
                // 记录内容以便重做时原样写回
                content = File.ReadAllBytes(longPath);
            }
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(MainViewModel), $"读取新建项内容失败（重做将建空文件）：{ex.Message}");
        }

        _history.Push(new CreateHistoryEntry(createdDisplayPath, isDirectory, content, lastWriteUtc));
        RaiseHistoryChanged();
    }

    private void PushDeleteHistory()
    {
        IReadOnlyList<RecycleBinEntry> batch = _recycleBin.LastBatchEntries;
        if (batch.Count == 0)
        {
            return;
        }

        _history.Push(new DeleteHistoryEntry(batch.ToList()));
        RaiseHistoryChanged();
    }

    private void PushTransferHistory(OperationHistoryContext? history, OperationResult result)
    {
        if (history is null || history.SourcePaths.Count == 0)
        {
            return;
        }

        var entries = new List<HistoryEntry>();

        foreach (string source in history.SourcePaths)
        {
            string name = PathHelper.GetName(source);
            string target = PathHelper.Combine(history.TargetDirectory, name);

            // 目标必须真的存在，冲突改名 / 跳过时不猜系统生成的名称
            if (!TryGetPathInfoCore(target, out DateTime lastWriteUtc, out _))
            {
                continue;
            }

            entries.Add(history.Kind == OperationHistoryKind.Copy
                ? new CopyHistoryEntry(source, target, lastWriteUtc)
                : new MoveHistoryEntry(source, target, lastWriteUtc));
        }

        if (entries.Count == 0)
        {
            return;
        }

        _history.Push(entries.Count == 1
            ? entries[0]
            : new BatchHistoryEntry(history.Kind, entries));
        RaiseHistoryChanged();
    }

    private static bool TryGetPathInfoCore(string displayPath, out DateTime lastWriteUtc, out bool isDirectory)
    {
        lastWriteUtc = DateTime.MinValue;
        isDirectory = false;

        if (string.IsNullOrWhiteSpace(displayPath))
        {
            return false;
        }

        try
        {
            string longPath = PathHelper.ToLong(displayPath);
            if (!FileOperations.TryGetEntryInfo(longPath, out uint attributes, out _, out DateTime utc))
            {
                return false;
            }

            isDirectory = (attributes & NativeMethods.FileAttributeDirectory) != 0;
            lastWriteUtc = utc;
            return true;
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(MainViewModel), $"查询条目信息失败（{displayPath}）：{ex.Message}");
            return false;
        }
    }

    // 撤销 / 重做只调用既有能力，也不再压入新历史（避免递归）
    private sealed class HistoryOperationsBridge : IHistoryOperations
    {
        private readonly MainViewModel _viewModel;

        public HistoryOperationsBridge(MainViewModel viewModel) => _viewModel = viewModel;

        public bool TryGetPathInfo(string displayPath, out DateTime lastWriteUtc, out bool isDirectory)
            => TryGetPathInfoCore(displayPath, out lastWriteUtc, out isDirectory);

        public FileOperationResult Rename(string sourceDisplayPath, string newName)
        {
            try
            {
                return FileOperations.Rename(PathHelper.ToLong(sourceDisplayPath), newName);
            }
            catch (Exception ex)
            {
                LogManager.Warning(typeof(MainViewModel), $"撤销 / 重做重命名失败：{ex.Message}");
                return FileOperationResult.Failed(Win32ErrorMessages.ErrorInvalidParameter, ex.Message);
            }
        }

        public FileTransferResult Move(IReadOnlyList<string> sourceDisplayPaths, string targetFolderDisplayPath)
            => _viewModel._coordinator.Move(sourceDisplayPaths, targetFolderDisplayPath);

        public FileTransferResult Copy(IReadOnlyList<string> sourceDisplayPaths, string targetFolderDisplayPath)
            => _viewModel._coordinator.Copy(sourceDisplayPaths, targetFolderDisplayPath);

        public HistoryDeleteResult DeleteToRecycleBin(IReadOnlyList<string> displayPaths)
        {
            // 静默移入回收站：不发提示条、不影响「刚才那批可撤销删除」
            HistoryDeleteResult result = _viewModel._recycleBin.MoveToRecycleBinQuiet(displayPaths);
            return result;
        }

        public HistoryRestoreResult RestoreFromRecycleBin(IReadOnlyList<RecycleBinEntry> entries)
        {
            RecycleBinOperationResult result = _viewModel._recycleBin.Restore(entries);
            return result.Changed || result.RestoredCount > 0
                ? HistoryRestoreResult.Ok(result.Message)
                : HistoryRestoreResult.Failed(result.Message);
        }

        public bool TryWriteFile(string displayPath, byte[] content, DateTime lastWriteUtc)
        {
            try
            {
                string longPath = PathHelper.ToLong(displayPath);
                using (var stream = new FileStream(longPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(content, 0, content.Length);
                }

                if (lastWriteUtc != DateTime.MinValue)
                {
                    try
                    {
                        File.SetLastWriteTimeUtc(longPath, lastWriteUtc);
                    }
                    catch (Exception ex)
                    {
                        LogManager.Warning(typeof(MainViewModel), $"设置重做文件的时间失败：{ex.Message}");
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                LogManager.Warning(typeof(MainViewModel), $"重做新建文件写入失败（{displayPath}）：{ex.Message}");
                return false;
            }
        }

        public FileOperationResult CreateDirectory(string parentDisplayPath, string name)
        {
            try
            {
                return FileOperations.CreateDirectory(parentDisplayPath, name);
            }
            catch (Exception ex)
            {
                LogManager.Warning(typeof(MainViewModel), $"重做新建文件夹失败：{ex.Message}");
                return FileOperationResult.Failed(Win32ErrorMessages.ErrorAccessDenied, ex.Message);
            }
        }
    }
}
