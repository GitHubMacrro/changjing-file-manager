using ChangJing.App.Core.LongPath;

namespace ChangJing.App.Core.Operations;

// 全按字符串处理，不用 System.IO.Path
internal static class TransferNaming
{
    /// <summary>同目录复制时追加的副本后缀</summary>
    public const string CopySuffix = " - 副本";

    public const string TempSuffix = ".cjtmp";

    /// <summary>扩展名前插入，无扩展名则直接追加</summary>
    public static string InsertBeforeExtension(string name, string insertion)
    {
        if (string.IsNullOrEmpty(name))
        {
            return insertion;
        }

        string extension = PathHelper.GetExtension(name);
        if (extension.Length == 0)
        {
            return name + insertion;
        }

        string stem = name.Substring(0, name.Length - extension.Length);
        return stem + insertion + extension;
    }

    /// <summary>生成同目录副本名，直到不重名</summary>
    public static string BuildCopyName(string targetDirectoryLong, string sourceName)
    {
        string first = InsertBeforeExtension(sourceName, CopySuffix);
        if (!ExistsIn(targetDirectoryLong, first))
        {
            return first;
        }

        for (int index = 2; index < 10000; index++)
        {
            string candidate = InsertBeforeExtension(sourceName, $"{CopySuffix} ({index})");
            if (!ExistsIn(targetDirectoryLong, candidate))
            {
                return candidate;
            }
        }

        return InsertBeforeExtension(sourceName, $"{CopySuffix} ({Environment.TickCount})");
    }

    /// <summary>生成保留两者用的名称，直到不重名</summary>
    public static string BuildKeepBothName(string targetDirectoryLong, string targetName)
    {
        for (int index = 2; index < 10000; index++)
        {
            string candidate = InsertBeforeExtension(targetName, $" ({index})");
            if (!ExistsIn(targetDirectoryLong, candidate))
            {
                return candidate;
            }
        }

        return InsertBeforeExtension(targetName, $" ({Environment.TickCount})");
    }

    /// <summary>生成不冲突的临时文件扩展路径</summary>
    public static string BuildTempPath(string targetDirectoryLong, string targetName)
    {
        string baseName = targetName.Length + TempSuffix.Length <= 255
            ? targetName + TempSuffix
            : targetName.Substring(0, Math.Max(1, 255 - TempSuffix.Length)) + TempSuffix;

        string candidate = PathHelper.Combine(targetDirectoryLong, baseName);
        if (!FileOperations.Exists(candidate))
        {
            return candidate;
        }

        for (int index = 2; index < 10000; index++)
        {
            string numbered = PathHelper.Combine(targetDirectoryLong, $"{baseName} ({index})");
            if (!FileOperations.Exists(numbered))
            {
                return numbered;
            }
        }

        return PathHelper.Combine(targetDirectoryLong, $"{baseName} ({Environment.TickCount})");
    }

    private static bool ExistsIn(string directoryLong, string name)
        => FileOperations.Exists(PathHelper.Combine(directoryLong, name));
}
