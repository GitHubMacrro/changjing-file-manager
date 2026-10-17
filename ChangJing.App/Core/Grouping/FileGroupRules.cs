namespace ChangJing.App.Core.Grouping;

public enum FileGroupBy
{
    None,

    /// <summary>名称按首字母：0-9 / A-H / I-P / Q-Z / 其他</summary>
    Name,

    /// <summary>修改日期：今天 / 昨天 / 本周早些时候 / … / 很久以前</summary>
    DateModified,

    Type,

    /// <summary>大小：空 / 极小 / 小 / 中 / 大 / 特大 / 巨大，文件夹单独一组</summary>
    Size,

    /// <summary>所在文件夹，仅搜索结果视图</summary>
    Folder,
}

/// <summary>分组键：Name 决定归属，Order + Direction 决定组的排序</summary>
public readonly struct FileGroupKey : IComparable<FileGroupKey>, IEquatable<FileGroupKey>
{
    public FileGroupKey(string name, int order, int direction)
    {
        Name = name;
        Order = order;
        Direction = direction < 0 ? -1 : 1;
    }

    public string Name { get; }

    public int Order { get; }

    /// <summary>排序方向：+1 升序、-1 降序</summary>
    public int Direction { get; }

    /// <inheritdoc/>
    public int CompareTo(FileGroupKey other)
    {
        int result = Order.CompareTo(other.Order);
        if (result == 0)
        {
            result = string.Compare(Name, other.Name, StringComparison.CurrentCultureIgnoreCase);
        }

        return Direction < 0 ? -result : result;
    }

    /// <inheritdoc/>
    public bool Equals(FileGroupKey other) => string.Equals(Name, other.Name, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is FileGroupKey other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Name.GetHashCode(StringComparison.Ordinal);

    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>分组规则，纯逻辑：把一个条目映射到 FileGroupKey</summary>
public static class FileGroupRules
{
    /// <summary>当前时间来源，验证时可替换为固定值</summary>
    public static Func<DateTime> NowProvider { get; set; } = () => DateTime.Now;

    public const string OtherGroupName = "其他";

    public const string FolderGroupName = "文件夹";

    public static FileGroupKey GetKey(
        FileGroupBy by,
        string name,
        bool isDirectory,
        DateTime lastWriteTime,
        ulong sizeBytes,
        string typeName,
        string searchFolderPath,
        bool descending,
        DateTime now)
    {
        int direction = descending ? -1 : 1;

        switch (by)
        {
            case FileGroupBy.Name:
                return NameGroup(name, isDirectory, direction);

            case FileGroupBy.DateModified:
                return DateGroup(lastWriteTime, now, direction);

            case FileGroupBy.Size:
                return SizeGroup(isDirectory, sizeBytes, direction);

            case FileGroupBy.Type:
                return TypeGroup(typeName, isDirectory, direction);

            case FileGroupBy.Folder:
                return FolderGroup(searchFolderPath, direction);

            default:
                return new FileGroupKey(string.Empty, 0, direction);
        }
    }

    /// <summary>名称分组：0-9 / A-H / I-P / Q-Z / 其他</summary>
    private static FileGroupKey NameGroup(string name, bool isDirectory, int direction)
    {
        _ = isDirectory;

        char first = FirstSignificantChar(name);
        if (first == '\0')
        {
            return new FileGroupKey(OtherGroupName, 4, direction);
        }

        if (first is >= '0' and <= '9')
        {
            return new FileGroupKey("0-9", 0, direction);
        }

        char upper = char.ToUpperInvariant(first);
        if (upper is >= 'A' and <= 'H')
        {
            return new FileGroupKey("A-H", 1, direction);
        }

        if (upper is >= 'I' and <= 'P')
        {
            return new FileGroupKey("I-P", 2, direction);
        }

        if (upper is >= 'Q' and <= 'Z')
        {
            return new FileGroupKey("Q-Z", 3, direction);
        }

        // 中文等脚本拿不到拼音首字母，只能归「其他」
        return new FileGroupKey(OtherGroupName, 4, direction);
    }

    private static char FirstSignificantChar(string name)
    {
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                return c;
            }
        }

        return '\0';
    }

    private static FileGroupKey DateGroup(DateTime value, DateTime now, int direction)
    {
        DateTime today = now.Date;
        DateTime valueDay = value == DateTime.MinValue ? DateTime.MinValue : value.Date;

        if (value == DateTime.MinValue)
        {
            return new FileGroupKey("很久以前", 7, direction);
        }

        // 周一为一周之始
        int offset = ((int)today.DayOfWeek + 6) % 7;
        DateTime weekStart = today.AddDays(-offset);
        DateTime yesterday = today.AddDays(-1);

        if (valueDay == today)
        {
            return new FileGroupKey("今天", 0, direction);
        }

        if (valueDay == yesterday)
        {
            return new FileGroupKey("昨天", 1, direction);
        }

        if (valueDay >= weekStart && valueDay < yesterday)
        {
            return new FileGroupKey("本周早些时候", 2, direction);
        }

        if (valueDay >= weekStart.AddDays(-7) && valueDay < weekStart)
        {
            return new FileGroupKey("上周", 3, direction);
        }

        if (valueDay.Year == today.Year && valueDay.Month == today.Month)
        {
            return new FileGroupKey("本月早些时候", 4, direction);
        }

        DateTime monthStart = new DateTime(today.Year, today.Month, 1);
        DateTime lastMonthStart = monthStart.AddMonths(-1);
        if (valueDay >= lastMonthStart && valueDay < monthStart)
        {
            return new FileGroupKey("上个月", 5, direction);
        }

        if (valueDay.Year == today.Year)
        {
            return new FileGroupKey("今年早些时候", 6, direction);
        }

        return new FileGroupKey("很久以前", 7, direction);
    }

    /// <summary>大小分组：文件夹单独一组，文件按大小区间</summary>
    private static FileGroupKey SizeGroup(bool isDirectory, ulong sizeBytes, int direction)
    {
        if (isDirectory)
        {
            return new FileGroupKey(FolderGroupName, -1, direction);
        }

        const ulong kb = 1024UL;
        const ulong mb = 1024UL * 1024;
        const ulong gb = 1024UL * 1024 * 1024;

        if (sizeBytes == 0)
        {
            return new FileGroupKey("空 (0)", 0, direction);
        }

        if (sizeBytes < 16 * kb)
        {
            return new FileGroupKey("极小 (< 16 KB)", 1, direction);
        }

        if (sizeBytes < 1 * mb)
        {
            return new FileGroupKey("小 (16 KB – 1 MB)", 2, direction);
        }

        if (sizeBytes < 128 * mb)
        {
            return new FileGroupKey("中 (1 – 128 MB)", 3, direction);
        }

        if (sizeBytes < 1 * gb)
        {
            return new FileGroupKey("大 (128 MB – 1 GB)", 4, direction);
        }

        if (sizeBytes < 4 * gb)
        {
            return new FileGroupKey("特大 (1 – 4 GB)", 5, direction);
        }

        return new FileGroupKey("巨大 (> 4 GB)", 6, direction);
    }

    private static FileGroupKey TypeGroup(string typeName, bool isDirectory, int direction)
    {
        if (isDirectory)
        {
            return new FileGroupKey(FolderGroupName, 0, direction);
        }

        string display = string.IsNullOrWhiteSpace(typeName) ? "未知类型" : typeName;
        // 不给固定序号，靠 CompareTo 在序号相同时按组名比较
        return new FileGroupKey(display, 0, direction);
    }

    /// <summary>「所在文件夹」分组，搜索结果视图</summary>
    private static FileGroupKey FolderGroup(string searchFolderPath, int direction)
    {
        string display = string.IsNullOrEmpty(searchFolderPath) ? "当前文件夹" : searchFolderPath;
        return new FileGroupKey(display, 0, direction);
    }
}
