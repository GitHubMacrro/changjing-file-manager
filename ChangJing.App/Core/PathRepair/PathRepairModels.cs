namespace ChangJing.App.Core.PathRepair;

/// <summary>「修复路径」检测到的问题类型，可组合</summary>
[Flags]
public enum PathRepairIssueKind
{
    None = 0,

    /// <summary>完整普通路径超过 259 字符</summary>
    TooLong = 1,

    ReservedName = 2,

    TrailingDot = 4,

    TrailingSpace = 8,

    /// <summary>名称含资源管理器不接受的字符</summary>
    InvalidCharacters = 16,
}

/// <summary>「修复路径」扫描命中的一条问题项</summary>
public sealed class PathRepairCandidate
{
    public PathRepairCandidate(
        string name,
        string displayPath,
        string longPath,
        string parentFolderDisplayPath,
        bool isDirectory,
        PathRepairIssueKind issues,
        string issueText,
        int currentLength,
        PathRepairSuggestion suggestion)
    {
        Name = name;
        DisplayPath = displayPath;
        LongPath = longPath;
        ParentFolderDisplayPath = parentFolderDisplayPath;
        IsDirectory = isDirectory;
        Issues = issues;
        IssueText = issueText;
        CurrentLength = currentLength;
        Suggestion = suggestion;
    }

    public string Name { get; }

    /// <summary>完整普通路径，不含 \\?\ 前缀</summary>
    public string DisplayPath { get; }

    /// <summary>完整扩展路径</summary>
    public string LongPath { get; }

    /// <summary>所在文件夹的普通路径</summary>
    public string ParentFolderDisplayPath { get; }

    public bool IsDirectory { get; }

    public PathRepairIssueKind Issues { get; }

    public string IssueText { get; }

    public int CurrentLength { get; }

    public PathRepairSuggestion Suggestion { get; }
}

/// <summary>一条问题项的建议修复方案</summary>
public sealed class PathRepairSuggestion
{
    public PathRepairSuggestion(
        string name,
        string displayPath,
        int length,
        string? folderShortenName,
        string? folderShortenNote,
        bool stillTooLong)
    {
        Name = name;
        DisplayPath = displayPath;
        Length = length;
        FolderShortenName = folderShortenName;
        FolderShortenNote = folderShortenNote;
        StillTooLong = stillTooLong;
    }

    /// <summary>建议的新名称，只改名不含路径</summary>
    public string Name { get; }

    /// <summary>建议的新完整普通路径</summary>
    public string DisplayPath { get; }

    public int Length { get; }

    /// <summary>「缩短所在文件夹名」建议；不需要时为 null</summary>
    public string? FolderShortenName { get; }

    /// <summary>「缩短所在文件夹名」的中文提示；不需要时为空串</summary>
    public string? FolderShortenNote { get; }

    /// <summary>仅靠改名仍无法降到 250 以内，需要同时缩短上级文件夹</summary>
    public bool StillTooLong { get; }
}

/// <summary>一次「修复路径」扫描的汇总</summary>
public sealed class PathRepairScanResult
{
    public PathRepairScanResult(
        int problemCount,
        int scannedCount,
        int skippedDirectoryCount,
        int skippedReparsePointCount,
        bool canceled,
        string? error)
    {
        ProblemCount = problemCount;
        ScannedCount = scannedCount;
        SkippedDirectoryCount = skippedDirectoryCount;
        SkippedReparsePointCount = skippedReparsePointCount;
        Canceled = canceled;
        Error = error;
    }

    public int ProblemCount { get; }

    public int ScannedCount { get; }

    /// <summary>跳过的目录数，无权限或打不开</summary>
    public int SkippedDirectoryCount { get; }

    /// <summary>跳过的 junction / 符号链接数，不下钻</summary>
    public int SkippedReparsePointCount { get; }

    public bool Canceled { get; }

    /// <summary>根目录打不开时的中文原因；否则 <c>null</c></summary>
    public string? Error { get; }
}

/// <summary>「修复路径」计划里的一项改名</summary>
public sealed class PathRepairPlanItem
{
    public PathRepairPlanItem(string originalDisplayPath, string originalName, string parentDisplayPath, string newName)
    {
        OriginalDisplayPath = originalDisplayPath;
        OriginalName = originalName;
        ParentDisplayPath = parentDisplayPath;
        NewName = newName;
    }

    /// <summary>原完整普通路径</summary>
    public string OriginalDisplayPath { get; }

    public string OriginalName { get; }

    /// <summary>原所在文件夹普通路径</summary>
    public string ParentDisplayPath { get; }

    public string NewName { get; }
}

/// <summary>「修复路径」计划里的一项上级文件夹改名</summary>
public sealed class PathRepairFolderRename
{
    public PathRepairFolderRename(string originalFolderDisplayPath, string newFolderName)
    {
        OriginalFolderDisplayPath = originalFolderDisplayPath;
        NewFolderName = newFolderName;
    }

    /// <summary>原文件夹普通路径</summary>
    public string OriginalFolderDisplayPath { get; }

    public string NewFolderName { get; }
}

/// <summary>一次「修复路径」改名的执行计划</summary>
public sealed class PathRepairPlan
{
    public List<PathRepairPlanItem> Items { get; } = new();

    /// <summary>要一并缩短的上级文件夹，先执行路径映射随后生效</summary>
    public List<PathRepairFolderRename> FolderRenames { get; } = new();
}
