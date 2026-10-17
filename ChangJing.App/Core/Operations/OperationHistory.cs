using ChangJing.App.Core.LongPath;
using ChangJing.App.Core.RecycleBin;

namespace ChangJing.App.Core.Operations;

public enum OperationHistoryKind
{
    Rename,

    Move,

    Copy,

    Create,

    Delete,
}

public readonly struct HistoryStepResult
{
    private HistoryStepResult(bool success, bool skipped, string message)
    {
        Success = success;
        Skipped = skipped;
        Message = message;
    }

    public bool Success { get; }

    /// <summary>是否因外部改动而跳过</summary>
    public bool Skipped { get; }

    public string Message { get; }

    public static HistoryStepResult Ok(string message) => new(true, false, message);

    public static HistoryStepResult Skip(string message) => new(false, true, message);

    public static HistoryStepResult Failed(string message) => new(false, false, message);
}

public readonly struct HistoryDeleteResult
{
    private HistoryDeleteResult(bool started, string message)
    {
        Started = started;
        Message = message;
    }

    public bool Started { get; }

    public string Message { get; }

    public static HistoryDeleteResult Ok(string message) => new(true, message);

    public static HistoryDeleteResult Rejected(string message) => new(false, message);
}

public readonly struct HistoryRestoreResult
{
    private HistoryRestoreResult(bool success, string message)
    {
        Success = success;
        Message = message;
    }

    public bool Success { get; }

    public string Message { get; }

    public static HistoryRestoreResult Ok(string message) => new(true, message);

    public static HistoryRestoreResult Failed(string message) => new(false, message);
}

/// <summary>操作历史栈执行撤销 / 重做时需要的文件系统能力</summary>
// 历史栈不直接复制移动，复用协调器
public interface IHistoryOperations
{
    /// <summary>查询路径修改时间与是否文件夹</summary>
    bool TryGetPathInfo(string displayPath, out DateTime lastWriteUtc, out bool isDirectory);

    FileOperationResult Rename(string sourceDisplayPath, string newName);

    /// <summary>移动若干条目到目标目录，异步</summary>
    FileTransferResult Move(IReadOnlyList<string> sourceDisplayPaths, string targetFolderDisplayPath);

    /// <summary>复制若干条目到目标目录，异步</summary>
    FileTransferResult Copy(IReadOnlyList<string> sourceDisplayPaths, string targetFolderDisplayPath);

    /// <summary>把若干条目移到本程序回收站</summary>
    HistoryDeleteResult DeleteToRecycleBin(IReadOnlyList<string> displayPaths);

    HistoryRestoreResult RestoreFromRecycleBin(IReadOnlyList<RecycleBinEntry> entries);

    /// <summary>写回字节内容并设置修改时间</summary>
    bool TryWriteFile(string displayPath, byte[] content, DateTime lastWriteUtc);

    FileOperationResult CreateDirectory(string parentDisplayPath, string name);
}

/// <summary>内存操作历史栈，最多 <see cref="MaxSteps"/> 步</summary>
// 对齐资源管理器 Ctrl+Z/Y，新操作清空重做栈
public sealed class OperationHistory
{
    public const int MaxSteps = 20;

    private readonly List<HistoryEntry> _undo = new();
    private readonly List<HistoryEntry> _redo = new();

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>撤销步骤动作名，无则空串</summary>
    public string UndoLabel => _undo.Count > 0 ? _undo[^1].KindText : string.Empty;

    /// <summary>重做步骤动作名，无则空串</summary>
    public string RedoLabel => _redo.Count > 0 ? _redo[^1].KindText : string.Empty;

    public int UndoCount => _undo.Count;

    public int RedoCount => _redo.Count;

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    public void Push(HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _redo.Clear();
        _undo.Add(entry);

        // 超过上限丢弃最旧的一步
        while (_undo.Count > MaxSteps)
        {
            _undo.RemoveAt(0);
        }
    }

    public HistoryStepResult Undo(IHistoryOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

        if (_undo.Count == 0)
        {
            return HistoryStepResult.Failed("没有可撤销的操作。");
        }

        HistoryEntry entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        HistoryStepResult result = entry.Undo(operations);

        // 成功执行才进重做栈
        if (result.Success)
        {
            _redo.Add(entry);
        }

        return result;
    }

    public HistoryStepResult Redo(IHistoryOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

        if (_redo.Count == 0)
        {
            return HistoryStepResult.Failed("没有可重做的操作。");
        }

        HistoryEntry entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);

        HistoryStepResult result = entry.Redo(operations);

        if (result.Success)
        {
            _undo.Add(entry);
        }

        return result;
    }
}

public abstract class HistoryEntry
{
    public abstract OperationHistoryKind Kind { get; }

    /// <summary>动作名，用于菜单文案</summary>
    public string KindText => Kind switch
    {
        OperationHistoryKind.Rename => "重命名",
        OperationHistoryKind.Move => "移动",
        OperationHistoryKind.Copy => "复制",
        OperationHistoryKind.Create => "新建",
        OperationHistoryKind.Delete => "删除",
        _ => "操作",
    };

    public abstract HistoryStepResult Undo(IHistoryOperations operations);

    public abstract HistoryStepResult Redo(IHistoryOperations operations);

    /// <summary>把路径转成「父目录 + 名称」</summary>
    protected static (string Parent, string Name) Split(string displayPath)
    {
        string? parent = PathHelper.GetParent(displayPath);
        return (parent ?? string.Empty, PathHelper.GetName(displayPath));
    }

    /// <summary>目标是否被外部改动，看修改时间</summary>
    protected static bool IsExternallyChanged(
        IHistoryOperations operations,
        string displayPath,
        DateTime expectedLastWriteUtc)
    {
        if (!operations.TryGetPathInfo(displayPath, out DateTime actual, out _))
        {
            return true;
        }

        // 修改时间允许 2 秒误差，文件系统精度不同
        return Math.Abs((actual - expectedLastWriteUtc).TotalSeconds) > 2;
    }
}

/// <summary>重命名：撤销改回原名，重做再改</summary>
public sealed class RenameHistoryEntry : HistoryEntry
{
    public RenameHistoryEntry(string originalPath, string newPath, DateTime lastWriteUtc)
    {
        OriginalPath = originalPath;
        NewPath = newPath;
        LastWriteUtc = lastWriteUtc;
    }

    public string OriginalPath { get; }

    public string NewPath { get; }

    /// <summary>修改时间，撤销重做都用它校验</summary>
    public DateTime LastWriteUtc { get; }

    /// <inheritdoc/>
    public override OperationHistoryKind Kind => OperationHistoryKind.Rename;

    /// <inheritdoc/>
    public override HistoryStepResult Undo(IHistoryOperations operations)
    {
        if (IsExternallyChanged(operations, NewPath, LastWriteUtc))
        {
            return HistoryStepResult.Skip($"「{PathHelper.GetName(NewPath)}」已被外部改动，已跳过撤销重命名。");
        }

        (string _, string name) = Split(OriginalPath);
        FileOperationResult result = operations.Rename(NewPath, name);
        return result.Success
            ? HistoryStepResult.Ok($"已撤销重命名：{PathHelper.GetName(NewPath)} → {name}。")
            : HistoryStepResult.Failed($"撤销重命名失败：{result.Error}");
    }

    /// <inheritdoc/>
    public override HistoryStepResult Redo(IHistoryOperations operations)
    {
        if (IsExternallyChanged(operations, OriginalPath, LastWriteUtc))
        {
            return HistoryStepResult.Skip($"「{PathHelper.GetName(OriginalPath)}」已被外部改动，已跳过重做重命名。");
        }

        (string _, string name) = Split(NewPath);
        FileOperationResult result = operations.Rename(OriginalPath, name);
        return result.Success
            ? HistoryStepResult.Ok($"已重做重命名：{PathHelper.GetName(OriginalPath)} → {name}。")
            : HistoryStepResult.Failed($"重做重命名失败：{result.Error}");
    }
}

/// <summary>移动：撤销移回原目录，重做再移目标</summary>
public sealed class MoveHistoryEntry : HistoryEntry
{
    public MoveHistoryEntry(string sourcePath, string targetPath, DateTime lastWriteUtc)
    {
        SourcePath = sourcePath;
        TargetPath = targetPath;
        LastWriteUtc = lastWriteUtc;
    }

    public string SourcePath { get; }

    public string TargetPath { get; }

    public DateTime LastWriteUtc { get; }

    /// <inheritdoc/>
    public override OperationHistoryKind Kind => OperationHistoryKind.Move;

    /// <inheritdoc/>
    public override HistoryStepResult Undo(IHistoryOperations operations)
    {
        if (IsExternallyChanged(operations, TargetPath, LastWriteUtc))
        {
            return HistoryStepResult.Skip($"「{PathHelper.GetName(TargetPath)}」已被外部改动，已跳过撤销移动。");
        }

        (string parent, string _) = Split(SourcePath);
        FileTransferResult result = operations.Move(new[] { TargetPath }, parent);
        return result.Started
            ? HistoryStepResult.Ok($"正在撤销移动：{PathHelper.GetName(TargetPath)} → {parent}")
            : HistoryStepResult.Failed($"撤销移动失败：{result.Message}");
    }

    /// <inheritdoc/>
    public override HistoryStepResult Redo(IHistoryOperations operations)
    {
        if (IsExternallyChanged(operations, SourcePath, LastWriteUtc))
        {
            return HistoryStepResult.Skip($"「{PathHelper.GetName(SourcePath)}」已被外部改动，已跳过重做移动。");
        }

        (string parent, string _) = Split(TargetPath);
        FileTransferResult result = operations.Move(new[] { SourcePath }, parent);
        return result.Started
            ? HistoryStepResult.Ok($"正在重做移动：{PathHelper.GetName(SourcePath)} → {parent}")
            : HistoryStepResult.Failed($"重做移动失败：{result.Message}");
    }
}

/// <summary>复制：撤销移目标到回收站，重做再复制</summary>
public sealed class CopyHistoryEntry : HistoryEntry
{
    public CopyHistoryEntry(string sourcePath, string targetPath, DateTime lastWriteUtc)
    {
        SourcePath = sourcePath;
        TargetPath = targetPath;
        LastWriteUtc = lastWriteUtc;
    }

    /// <summary>源完整路径，重做仍从它复制</summary>
    public string SourcePath { get; }

    public string TargetPath { get; }

    public DateTime LastWriteUtc { get; }

    /// <inheritdoc/>
    public override OperationHistoryKind Kind => OperationHistoryKind.Copy;

    /// <inheritdoc/>
    public override HistoryStepResult Undo(IHistoryOperations operations)
    {
        if (IsExternallyChanged(operations, TargetPath, LastWriteUtc))
        {
            return HistoryStepResult.Skip($"「{PathHelper.GetName(TargetPath)}」已被外部改动，已跳过撤销复制。");
        }

        HistoryDeleteResult result = operations.DeleteToRecycleBin(new[] { TargetPath });
        return result.Started
            ? HistoryStepResult.Ok($"已撤销复制：{PathHelper.GetName(TargetPath)} 已移到回收站。")
            : HistoryStepResult.Failed($"撤销复制失败：{result.Message}");
    }

    /// <inheritdoc/>
    public override HistoryStepResult Redo(IHistoryOperations operations)
    {
        if (!operations.TryGetPathInfo(SourcePath, out _, out _))
        {
            return HistoryStepResult.Skip($"源「{PathHelper.GetName(SourcePath)}」已不存在，已跳过重做复制。");
        }

        (string parent, string _) = Split(TargetPath);
        FileTransferResult result = operations.Copy(new[] { SourcePath }, parent);
        return result.Started
            ? HistoryStepResult.Ok($"正在重做复制：{PathHelper.GetName(SourcePath)} → {parent}")
            : HistoryStepResult.Failed($"重做复制失败：{result.Message}");
    }
}

/// <summary>新建：撤销移回收站，重做重新创建</summary>
public sealed class CreateHistoryEntry : HistoryEntry
{
    public CreateHistoryEntry(string createdPath, bool isDirectory, byte[]? content, DateTime lastWriteUtc)
    {
        CreatedPath = createdPath;
        IsDirectory = isDirectory;
        Content = content;
        LastWriteUtc = lastWriteUtc;
    }

    public string CreatedPath { get; }

    public bool IsDirectory { get; }

    /// <summary>文件内容，读取失败时为 null</summary>
    public byte[]? Content { get; }

    public DateTime LastWriteUtc { get; }

    /// <inheritdoc/>
    public override OperationHistoryKind Kind => OperationHistoryKind.Create;

    /// <inheritdoc/>
    public override HistoryStepResult Undo(IHistoryOperations operations)
    {
        if (!operations.TryGetPathInfo(CreatedPath, out _, out _))
        {
            return HistoryStepResult.Skip($"「{PathHelper.GetName(CreatedPath)}」已不存在，已跳过撤销新建。");
        }

        HistoryDeleteResult result = operations.DeleteToRecycleBin(new[] { CreatedPath });
        return result.Started
            ? HistoryStepResult.Ok($"已撤销新建：{PathHelper.GetName(CreatedPath)} 已移到回收站。")
            : HistoryStepResult.Failed($"撤销新建失败：{result.Message}");
    }

    /// <inheritdoc/>
    public override HistoryStepResult Redo(IHistoryOperations operations)
    {
        if (operations.TryGetPathInfo(CreatedPath, out _, out _))
        {
            return HistoryStepResult.Skip($"「{PathHelper.GetName(CreatedPath)}」已存在，已跳过重做新建。");
        }

        if (IsDirectory)
        {
            (string parent, string name) = Split(CreatedPath);
            FileOperationResult created = operations.CreateDirectory(parent, name);
            return created.Success
                ? HistoryStepResult.Ok($"已重做新建文件夹：{name}。")
                : HistoryStepResult.Failed($"重做新建文件夹失败：{created.Error}");
        }

        bool ok = operations.TryWriteFile(CreatedPath, Content ?? Array.Empty<byte>(), LastWriteUtc);
        return ok
            ? HistoryStepResult.Ok($"已重做新建文件：{PathHelper.GetName(CreatedPath)}。")
            : HistoryStepResult.Failed($"重做新建文件失败：{PathHelper.GetName(CreatedPath)}。");
    }
}

/// <summary>删除到回收站：撤销还原，重做再删</summary>
public sealed class DeleteHistoryEntry : HistoryEntry
{
    public DeleteHistoryEntry(IReadOnlyList<RecycleBinEntry> entries)
    {
        Entries = entries;
    }

    /// <summary>被移入回收站的条目</summary>
    public IReadOnlyList<RecycleBinEntry> Entries { get; }

    /// <inheritdoc/>
    public override OperationHistoryKind Kind => OperationHistoryKind.Delete;

    /// <inheritdoc/>
    public override HistoryStepResult Undo(IHistoryOperations operations)
    {
        if (Entries.Count == 0)
        {
            return HistoryStepResult.Failed("没有可还原的项目。");
        }

        HistoryRestoreResult result = operations.RestoreFromRecycleBin(Entries);
        return result.Success
            ? HistoryStepResult.Ok(result.Message)
            : HistoryStepResult.Failed(result.Message);
    }

    /// <inheritdoc/>
    public override HistoryStepResult Redo(IHistoryOperations operations)
    {
        var paths = Entries.Select(entry => entry.OriginalPath).ToList();
        if (paths.Count == 0)
        {
            return HistoryStepResult.Failed("没有可删除的项目。");
        }

        HistoryDeleteResult result = operations.DeleteToRecycleBin(paths);
        return result.Started
            ? HistoryStepResult.Ok(result.Message)
            : HistoryStepResult.Failed(result.Message);
    }
}

/// <summary>同批操作合成一步，便于一次撤销</summary>
public sealed class BatchHistoryEntry : HistoryEntry
{
    private readonly OperationHistoryKind _kind;
    private readonly List<HistoryEntry> _entries;

    public BatchHistoryEntry(OperationHistoryKind kind, IReadOnlyList<HistoryEntry> entries)
    {
        _kind = kind;
        _entries = new List<HistoryEntry>(entries);
    }

    /// <inheritdoc/>
    public override OperationHistoryKind Kind => _kind;

    public int Count => _entries.Count;

    /// <inheritdoc/>
    public override HistoryStepResult Undo(IHistoryOperations operations)
    {
        if (_entries.Count == 0)
        {
            return HistoryStepResult.Failed("没有可撤销的项目。");
        }

        int ok = 0;
        int skipped = 0;
        var failures = new List<string>();

        // 逆序撤销，避免子项找不到上级
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            HistoryStepResult result = _entries[i].Undo(operations);
            if (result.Skipped)
            {
                skipped++;
            }
            else if (result.Success)
            {
                ok++;
            }
            else
            {
                failures.Add(result.Message);
            }
        }

        return Summarize("撤销", ok, skipped, failures);
    }

    /// <inheritdoc/>
    public override HistoryStepResult Redo(IHistoryOperations operations)
    {
        if (_entries.Count == 0)
        {
            return HistoryStepResult.Failed("没有可重做的项目。");
        }

        int ok = 0;
        int skipped = 0;
        var failures = new List<string>();

        foreach (HistoryEntry entry in _entries)
        {
            HistoryStepResult result = entry.Redo(operations);
            if (result.Skipped)
            {
                skipped++;
            }
            else if (result.Success)
            {
                ok++;
            }
            else
            {
                failures.Add(result.Message);
            }
        }

        return Summarize("重做", ok, skipped, failures);
    }

    private HistoryStepResult Summarize(string verb, int ok, int skipped, List<string> failures)
    {
        if (ok == 0 && failures.Count == 0)
        {
            // 全部因外部改动被跳过
            return HistoryStepResult.Skip($"已跳过{verb}{KindText}（{skipped} 个项目已被外部改动）。");
        }

        var parts = new List<string>(3);
        if (ok > 0)
        {
            parts.Add($"已{verb} {ok} 个项目的{KindText}");
        }

        if (skipped > 0)
        {
            parts.Add($"跳过 {skipped} 个（已被外部改动）");
        }

        if (failures.Count > 0)
        {
            parts.Add($"失败 {failures.Count} 个：{string.Join("；", failures)}");
        }

        string message = string.Join("；", parts) + "。";
        return failures.Count > 0 && ok == 0
            ? HistoryStepResult.Failed(message)
            : HistoryStepResult.Ok(message);
    }
}
