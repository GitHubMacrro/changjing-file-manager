using ChangJing.App.Core.PathRepair;

namespace ChangJing.App.ViewModels;

/// <summary>「修复路径」请求：有选中项时分析选中项本身，无选中项时递归扫描当前文件夹</summary>
public sealed class PathRepairRequestEventArgs : EventArgs
{
    public PathRepairRequestEventArgs(IReadOnlyList<string> selectedDisplayPaths, string scanFolderDisplayPath)
    {
        SelectedDisplayPaths = selectedDisplayPaths;
        ScanFolderDisplayPath = scanFolderDisplayPath;
    }

    /// <summary>选中的条目，普通路径</summary>
    public IReadOnlyList<string> SelectedDisplayPaths { get; }

    /// <summary>要递归扫描的文件夹，普通路径</summary>
    public string ScanFolderDisplayPath { get; }
}

/// <summary>「修复路径」执行时失败的一项</summary>
public sealed class PathRepairFailure
{
    public PathRepairFailure(string displayPath, string reason)
    {
        DisplayPath = displayPath;
        Reason = reason;
    }

    /// <summary>失败项的完整普通路径</summary>
    public string DisplayPath { get; }

    public string Reason { get; }

    public string Name => ChangJing.App.Core.LongPath.PathHelper.GetName(DisplayPath);

    public string ToolTipText => $"{DisplayPath}\n原因：{Reason}";
}

/// <summary>一次「修复路径」执行的结果</summary>
public sealed class PathRepairExecutionResult
{
    public PathRepairExecutionResult(
        bool started,
        int repairedCount,
        int failedCount,
        string message,
        bool moved,
        string? openFolderDisplayPath,
        IReadOnlyList<PathRepairFailure>? failures = null)
    {
        Started = started;
        RepairedCount = repairedCount;
        FailedCount = failedCount;
        Message = message;
        Moved = moved;
        OpenFolderDisplayPath = openFolderDisplayPath;
        Failures = failures ?? Array.Empty<PathRepairFailure>();
    }

    public bool Started { get; }

    public int RepairedCount { get; }

    public int FailedCount { get; }

    public string Message { get; }

    public bool Moved { get; }

    /// <summary>「打开所在文件夹」的目标，普通路径；无则为 <c>null</c></summary>
    public string? OpenFolderDisplayPath { get; }

    public IReadOnlyList<PathRepairFailure> Failures { get; }

    public static PathRepairExecutionResult Rejected(string message)
        => new(false, 0, 0, message, false, null);
}

/// <summary>「修复路径」执行能力的宿主接口，由主窗口视图模型实现</summary>
public interface IPathRepairHost
{
    /// <summary>执行一批改名，含可选的上级文件夹缩短</summary>
    PathRepairExecutionResult ExecuteRepairRenames(PathRepairPlan plan);

    PathRepairExecutionResult ExecuteRepairMove(IReadOnlyList<string> sourceDisplayPaths, string targetFolderDisplayPath);
}
