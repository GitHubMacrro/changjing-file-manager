using ChangJing.App.Core.FileSystem;

namespace ChangJing.App.Core.LongPath;

/// <summary>判定资源管理器无法处理的路径：超长、结尾点 / 空格、保留名</summary>
public static class LongPathRules
{
    /// <summary>资源管理器可处理的普通路径最大长度，即 MAX_PATH - 1</summary>
    public const int MaxExplorerPathLength = 259;

    /// <summary>列表「超长」徽章的显示阈值，与 MaxExplorerPathLength 同值</summary>
    public const int BadgePathLength = MaxExplorerPathLength;

    /// <summary>Windows 保留设备名，不区分大小写，带扩展名也算，如 CON.txt</summary>
    private static readonly string[] ReservedNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>判断普通路径是否超长，长度 &gt; 259</summary>
    public static bool IsTooLong(string? displayPath)
        => !string.IsNullOrEmpty(displayPath) && displayPath!.Length > MaxExplorerPathLength;

    /// <summary>判断名字是否以点或空格结尾，资源管理器无法处理</summary>
    public static bool HasTrailingDotOrSpace(string? name)
        => !string.IsNullOrEmpty(name) && (name![^1] == '.' || name[^1] == ' ');

    /// <summary>判断名字是否为 Windows 保留设备名，忽略大小写与扩展名</summary>
    public static bool IsReservedName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        // CON.txt、NUL. 等也算保留名：取第一个点之前的部分
        string stem = name!;
        int dot = stem.IndexOf('.');
        if (dot >= 0)
        {
            stem = stem.Substring(0, dot);
        }

        // "CON " 仍是 CON
        stem = stem.TrimEnd(' ');

        foreach (string reserved in ReservedNames)
        {
            if (string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static LongPathAnalysis Analyze(string? displayPath)
    {
        if (string.IsNullOrEmpty(displayPath))
        {
            return new LongPathAnalysis(string.Empty, 0, false, false, false, false, string.Empty);
        }

        string path = displayPath!;
        string name = ExtractName(path);
        bool tooLong = path.Length > MaxExplorerPathLength;
        bool trailingDot = name.Length > 0 && name[^1] == '.';
        bool trailingSpace = name.Length > 0 && name[^1] == ' ';
        bool reserved = IsReservedName(name);
        bool problematic = tooLong || trailingDot || trailingSpace || reserved;

        return new LongPathAnalysis(
            path,
            path.Length,
            tooLong,
            trailingDot,
            trailingSpace,
            reserved,
            problematic ? BuildReason(path.Length, trailingDot, trailingSpace, reserved) : string.Empty);
    }

    /// <summary>判定并返回原因文字；可处理时 reason 为空串</summary>
    public static bool TryDescribeIssue(string? displayPath, out string reason)
    {
        LongPathAnalysis analysis = Analyze(displayPath);
        reason = analysis.Reason;
        return analysis.IsProblematic;
    }

    public static bool ShouldShowBadge(string? displayPath)
    {
        if (string.IsNullOrEmpty(displayPath))
        {
            return false;
        }

        if (displayPath!.Length > BadgePathLength)
        {
            return true;
        }

        string name = ExtractName(displayPath!);
        return HasTrailingDotOrSpace(name) || IsReservedName(name);
    }

    /// <summary>徽章原因文字，中文；不该显示徽章时为空串</summary>
    public static string DescribeBadgeIssue(string? displayPath)
    {
        if (string.IsNullOrEmpty(displayPath) || !ShouldShowBadge(displayPath))
        {
            return string.Empty;
        }

        string path = displayPath!;
        string name = ExtractName(path);
        return BuildReason(
            path.Length,
            name.Length > 0 && name[^1] == '.',
            name.Length > 0 && name[^1] == ' ',
            IsReservedName(name));
    }

    private static string BuildReason(int length, bool trailingDot, bool trailingSpace, bool reserved)
    {
        var parts = new List<string>(4);

        if (length > MaxExplorerPathLength)
        {
            parts.Add($"路径过长（{length} / 上限 {MaxExplorerPathLength}）");
        }

        if (trailingDot)
        {
            parts.Add("名称以点结尾");
        }

        if (trailingSpace)
        {
            parts.Add("名称以空格结尾");
        }

        if (reserved)
        {
            parts.Add("名称是系统保留名");
        }

        return string.Join("、", parts) + "，Windows 资源管理器可能无法操作，建议在本工具中复制或移动。";
    }

    /// <summary>取路径最后一段名称，不裁剪结尾点与空格</summary>
    private static string ExtractName(string path)
    {
        int index = path.LastIndexOf('\\');
        return index < 0 ? path : path.Substring(index + 1);
    }
}

/// <summary>一条路径的「超长 / 资源管理器无法处理」分析结果</summary>
public readonly struct LongPathAnalysis
{
    internal LongPathAnalysis(
        string path,
        int length,
        bool isTooLong,
        bool hasTrailingDot,
        bool hasTrailingSpace,
        bool isReservedName,
        string reason)
    {
        Path = path;
        Length = length;
        IsTooLong = isTooLong;
        HasTrailingDot = hasTrailingDot;
        HasTrailingSpace = hasTrailingSpace;
        IsReservedName = isReservedName;
        Reason = reason;
    }

    /// <summary>被分析的普通路径，原样保留</summary>
    public string Path { get; }

    /// <summary>路径长度，单位字符</summary>
    public int Length { get; }

    /// <summary>是否超过 MaxExplorerPathLength</summary>
    public bool IsTooLong { get; }

    public bool HasTrailingDot { get; }

    public bool HasTrailingSpace { get; }

    public bool IsReservedName { get; }

    public bool IsProblematic => IsTooLong || HasTrailingDot || HasTrailingSpace || IsReservedName;

    /// <summary>原因文字，中文；可处理时为空串</summary>
    public string Reason { get; }
}
