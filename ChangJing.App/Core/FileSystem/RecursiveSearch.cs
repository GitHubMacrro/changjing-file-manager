namespace ChangJing.App.Core.FileSystem;

/// <summary>一次递归搜索命中的一条结果，不可变</summary>
public sealed class SearchHit
{
    public SearchHit(
        string name,
        string longPath,
        string displayPath,
        string folderDisplayPath,
        string folderLongPath,
        uint attributes,
        bool isDirectory,
        ulong size,
        DateTime lastWriteTimeUtc)
    {
        Name = name;
        LongPath = longPath;
        DisplayPath = displayPath;
        FolderDisplayPath = folderDisplayPath;
        FolderLongPath = folderLongPath;
        Attributes = attributes;
        IsDirectory = isDirectory;
        Size = size;
        LastWriteTimeUtc = lastWriteTimeUtc;
    }

    /// <summary>文件名，不含路径</summary>
    public string Name { get; }

    /// <summary>完整扩展路径</summary>
    public string LongPath { get; }

    /// <summary>界面显示用的完整普通路径，永不含 \\?\</summary>
    public string DisplayPath { get; }

    /// <summary>所在文件夹的普通路径</summary>
    public string FolderDisplayPath { get; }

    /// <summary>所在文件夹的扩展路径</summary>
    public string FolderLongPath { get; }

    /// <summary>Win32 文件属性位</summary>
    public uint Attributes { get; }

    public bool IsDirectory { get; }

    /// <summary>文件字节大小；文件夹恒为 0</summary>
    public ulong Size { get; }

    /// <summary>修改时间，UTC</summary>
    public DateTime LastWriteTimeUtc { get; }

    /// <inheritdoc/>
    public override string ToString() => $"{Name} — {FolderDisplayPath}";
}

public sealed class SearchProgress
{
    public SearchProgress(int matched, int scanned, int skippedDirectories, string currentDirectory)
    {
        Matched = matched;
        Scanned = scanned;
        SkippedDirectories = skippedDirectories;
        CurrentDirectory = currentDirectory;
    }

    public int Matched { get; }

    public int Scanned { get; }

    public int SkippedDirectories { get; }

    /// <summary>当前正在扫描的目录，普通路径</summary>
    public string CurrentDirectory { get; }
}

public sealed class SearchSummary
{
    public SearchSummary(
        int matchedCount,
        int scannedCount,
        int skippedDirectoryCount,
        int skippedReparsePointCount,
        bool canceled,
        string? error)
    {
        MatchedCount = matchedCount;
        ScannedCount = scannedCount;
        SkippedDirectoryCount = skippedDirectoryCount;
        SkippedReparsePointCount = skippedReparsePointCount;
        Canceled = canceled;
        Error = error;
    }

    public int MatchedCount { get; }

    public int ScannedCount { get; }

    public int SkippedDirectoryCount { get; }

    /// <summary>跳过的 junction / 符号链接数，不下钻</summary>
    public int SkippedReparsePointCount { get; }

    public bool Canceled { get; }

    /// <summary>根目录都打不开时的中文原因；否则 null</summary>
    public string? Error { get; }
}
