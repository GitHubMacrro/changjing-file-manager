using ChangJing.App.Core.LongPath;

namespace ChangJing.App.Core.Operations;

public enum ConflictAction
{
    /// <summary>仅文件对文件可替换</summary>
    Replace,

    Skip,

    /// <summary>给源改名保留两者</summary>
    KeepBoth,
}

public enum ConflictPairKind
{
    FileVsFile,

    /// <summary>源文件对目标文件夹，不能替换</summary>
    FileVsDirectory,

    /// <summary>源文件夹对目标文件，不能替换</summary>
    DirectoryVsFile,
}

/// <summary>同名冲突的上下文，交给对话框处理</summary>
// 路径都是普通形态，永不出现 \\?\
public sealed class ConflictInfo
{
    internal ConflictInfo(
        string sourceName,
        string sourceDisplayPath,
        ulong sourceSize,
        DateTime sourceLastWriteTime,
        string targetName,
        string targetDisplayPath,
        ulong targetSize,
        DateTime targetLastWriteTime,
        ConflictPairKind pairKind,
        int? remainingConflicts = null)
    {
        SourceName = sourceName;
        SourceDisplayPath = sourceDisplayPath;
        SourceSize = sourceSize;
        SourceLastWriteTime = sourceLastWriteTime;
        TargetName = targetName;
        TargetDisplayPath = targetDisplayPath;
        TargetSize = targetSize;
        TargetLastWriteTime = targetLastWriteTime;
        PairKind = pairKind;
        RemainingConflicts = remainingConflicts;
    }

    public string SourceName { get; }

    /// <summary>源条目完整普通路径</summary>
    public string SourceDisplayPath { get; }

    /// <summary>源文件大小，单位字节，文件夹为 0</summary>
    public ulong SourceSize { get; }

    public DateTime SourceLastWriteTime { get; }

    public string TargetName { get; }

    /// <summary>目标条目完整普通路径</summary>
    public string TargetDisplayPath { get; }

    /// <summary>目标文件大小，单位字节，文件夹为 0</summary>
    public ulong TargetSize { get; }

    public DateTime TargetLastWriteTime { get; }

    public ConflictPairKind PairKind { get; }

    /// <summary>剩余同名冲突数，未知时为 null</summary>
    public int? RemainingConflicts { get; }

    public bool SourceIsDirectory => PairKind == ConflictPairKind.DirectoryVsFile;

    public bool TargetIsDirectory => PairKind == ConflictPairKind.FileVsDirectory;

    /// <summary>仅文件对文件时可替换</summary>
    public bool CanReplace => PairKind == ConflictPairKind.FileVsFile;

    /// <summary>保留两者时源将改成的名称，供预览</summary>
    public string? SuggestedKeepBothName
    {
        get
        {
            try
            {
                string? targetDirectoryLong = PathHelper.GetParent(PathHelper.ToLong(TargetDisplayPath));
                if (targetDirectoryLong is null)
                {
                    return null;
                }

                return TransferNaming.BuildKeepBothName(targetDirectoryLong, SourceName);
            }
            catch
            {
                return null;
            }
        }
    }
}

/// <summary>界面返回的冲突决定</summary>
public readonly struct ConflictDecision
{
    public ConflictDecision(ConflictAction action, bool applyToAll = false)
    {
        Action = action;
        ApplyToAll = applyToAll;
    }

    public ConflictAction Action { get; }

    public bool ApplyToAll { get; }

    public static ConflictDecision Skip(bool applyToAll = false) => new(ConflictAction.Skip, applyToAll);

    public static ConflictDecision Replace(bool applyToAll = false) => new(ConflictAction.Replace, applyToAll);

    public static ConflictDecision KeepBoth(bool applyToAll = false) => new(ConflictAction.KeepBoth, applyToAll);
}
