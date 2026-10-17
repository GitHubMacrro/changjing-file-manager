using ChangJing.App.Core.LongPath;

namespace ChangJing.App.Core.PathRepair;

/// <summary>「修复路径」的纯逻辑规则：问题检测、建议新名称、名称校验</summary>
public static class PathRepairRules
{
    /// <summary>资源管理器能处理的普通路径最大长度</summary>
    public const int ExplorerMaxPathLength = LongPathRules.MaxExplorerPathLength;

    /// <summary>修复目标长度 250</summary>
    public const int TargetPathLength = 250;

    /// <summary>缩短文件名主体时至少保留的字符数</summary>
    public const int MinKeptNameLength = 8;

    /// <summary>中间截断用的省略号</summary>
    public const string Ellipsis = "…";

    /// <summary>省略号不可用时的兜底字符</summary>
    public const string Tilde = "~";

    private static readonly char[] InvalidNameChars = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    /// <summary>检测一条路径的问题类型；无问题返回 None</summary>
    public static PathRepairIssueKind DetectIssues(string name, string displayPath)
    {
        PathRepairIssueKind issues = PathRepairIssueKind.None;

        if (!string.IsNullOrEmpty(displayPath) && displayPath.Length > ExplorerMaxPathLength)
        {
            issues |= PathRepairIssueKind.TooLong;
        }

        if (LongPathRules.IsReservedName(name))
        {
            issues |= PathRepairIssueKind.ReservedName;
        }

        if (!string.IsNullOrEmpty(name))
        {
            if (name[^1] == '.')
            {
                issues |= PathRepairIssueKind.TrailingDot;
            }
            else if (name[^1] == ' ')
            {
                issues |= PathRepairIssueKind.TrailingSpace;
            }
        }

        if (HasInvalidCharacters(name))
        {
            issues |= PathRepairIssueKind.InvalidCharacters;
        }

        return issues;
    }

    /// <summary>是否含资源管理器不接受的字符</summary>
    public static bool HasInvalidCharacters(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        foreach (char c in name!)
        {
            if (c < ' ' || Array.IndexOf(InvalidNameChars, c) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>把问题类型转成中文描述</summary>
    public static string DescribeIssues(PathRepairIssueKind issues)
    {
        if (issues == PathRepairIssueKind.None)
        {
            return string.Empty;
        }

        var parts = new List<string>(4);
        if (issues.HasFlag(PathRepairIssueKind.TooLong))
        {
            parts.Add("路径过长");
        }

        if (issues.HasFlag(PathRepairIssueKind.ReservedName))
        {
            parts.Add("系统保留名");
        }

        if (issues.HasFlag(PathRepairIssueKind.TrailingDot))
        {
            parts.Add("以点结尾");
        }

        if (issues.HasFlag(PathRepairIssueKind.TrailingSpace))
        {
            parts.Add("以空格结尾");
        }

        if (issues.HasFlag(PathRepairIssueKind.InvalidCharacters))
        {
            parts.Add("含无效字符");
        }

        return string.Join("、", parts);
    }

    /// <summary>生成问题项的建议修复方案，必要时附缩短上级文件夹建议</summary>
    public static PathRepairSuggestion Suggest(string name, string parentFolderDisplayPath, bool isDirectory)
    {
        _ = isDirectory;

        string fixedName = FixNameIssues(name);

        int parentLength = parentFolderDisplayPath.Length;
        string suggestedName = fixedName;
        string? folderShortenName = null;
        string? folderShortenNote = null;
        bool stillTooLong = false;

        int maxNameLength = TargetPathLength - parentLength - 1;

        if (parentLength + 1 + fixedName.Length > TargetPathLength || maxNameLength < MinKeptNameLength)
        {
            if (maxNameLength >= MinKeptNameLength)
            {
                suggestedName = BuildShortName(fixedName, maxNameLength);
            }
            else
            {
                // 改名不够，需同时缩短上级文件夹
                string parentName = PathHelper.GetName(parentFolderDisplayPath);
                string? grandParent = PathHelper.GetParent(parentFolderDisplayPath);
                int grandParentLength = grandParent is null ? 0 : grandParent.Length;

                int desiredNameLength = Math.Max(Math.Min(fixedName.Length, 32), MinKeptNameLength);
                int newParentMax = TargetPathLength - grandParentLength - 1 - 1 - desiredNameLength;
                string shortenedFolder = BuildShortName(parentName, Math.Max(newParentMax, 1));

                if (!string.Equals(shortenedFolder, parentName, StringComparison.Ordinal))
                {
                    folderShortenName = shortenedFolder;
                    int reduction = parentName.Length - shortenedFolder.Length;
                    int effectiveParentLength = Math.Max(1, parentLength - reduction);
                    int effectiveMax = TargetPathLength - effectiveParentLength - 1;
                    suggestedName = effectiveMax > 0
                        ? BuildShortName(fixedName, effectiveMax)
                        : BuildShortName(fixedName, desiredNameLength);
                    folderShortenNote = $"建议同时缩短上级文件夹「{parentName}」为「{shortenedFolder}」";
                }
                else
                {
                    suggestedName = BuildShortName(fixedName, Math.Max(1, maxNameLength));
                }
            }
        }

        string effectiveParentPath = parentFolderDisplayPath;
        if (folderShortenName is not null)
        {
            string? grandParent = PathHelper.GetParent(parentFolderDisplayPath);
            effectiveParentPath = (grandParent is null ? string.Empty : grandParent.TrimEnd('\\') + "\\") + folderShortenName;
        }

        string suggestedPath = effectiveParentPath.TrimEnd('\\') + "\\" + suggestedName;
        int suggestedLength = suggestedPath.Length;
        stillTooLong = suggestedLength > TargetPathLength;

        return new PathRepairSuggestion(
            suggestedName,
            suggestedPath,
            suggestedLength,
            folderShortenName,
            folderShortenNote,
            stillTooLong);
    }

    /// <summary>修正名称问题：去尾随点 / 空格、保留名加 _、替换无效字符</summary>
    public static string FixNameIssues(string name)
    {
        string working = name ?? string.Empty;

        if (HasInvalidCharacters(working))
        {
            var builder = new System.Text.StringBuilder(working.Length);
            foreach (char c in working)
            {
                builder.Append(c < ' ' || Array.IndexOf(InvalidNameChars, c) >= 0 ? '_' : c);
            }

            working = builder.ToString();
        }

        working = working.TrimEnd('.', ' ');

        if (working.Length == 0)
        {
            working = "_";
        }

        // 保留名加 "_"，保留扩展名
        if (LongPathRules.IsReservedName(working))
        {
            string extension = PathHelper.GetExtension(working);
            string stem = working.Substring(0, working.Length - extension.Length);
            working = stem + "_" + extension;
        }

        return working;
    }

    /// <summary>把名称缩到 maxLength 内：保留扩展名、中间截断加 …、太长用 ~ 兜底</summary>
    public static string BuildShortName(string name, int maxLength)
    {
        if (maxLength <= 0)
        {
            return Tilde;
        }

        if (name.Length <= maxLength)
        {
            return name;
        }

        string extension = PathHelper.GetExtension(name);
        string stem = name.Substring(0, name.Length - extension.Length);

        if (extension.Length >= maxLength)
        {
            return name.Substring(0, Math.Max(0, maxLength - Tilde.Length)) + Tilde;
        }

        int stemBudget = maxLength - extension.Length;
        if (stemBudget <= 0)
        {
            return Tilde + extension;
        }

        if (stemBudget == 1)
        {
            // 放不下省略号时用 ~ 兜底
            return Tilde + extension;
        }

        if (stem.Length <= stemBudget)
        {
            return stem + extension;
        }

        int keep = stemBudget - 1;
        if (keep <= 0)
        {
            return Tilde + extension;
        }

        int head = (keep + 1) / 2;
        int tail = keep / 2;

        string result = stem.Substring(0, head) + Ellipsis + (tail > 0 ? stem.Substring(stem.Length - tail) : string.Empty) + extension;
        if (result.Length > maxLength)
        {
            return Tilde + extension;
        }

        return result;
    }

    /// <summary>实时校验用户编辑后的建议名称</summary>
    public static PathRepairValidation Validate(string? newName, string parentFolderDisplayPath, Func<string, bool>? isNameTaken)
    {
        string name = newName ?? string.Empty;

        if (name.Trim().Length == 0)
        {
            return PathRepairValidation.Invalid("名称不能为空。");
        }

        if (name.Length > 255)
        {
            return PathRepairValidation.Invalid($"名称过长（{name.Length} / 上限 255 个字符）。");
        }

        if (HasInvalidCharacters(name))
        {
            return PathRepairValidation.Invalid("名称含资源管理器不接受的字符。");
        }

        if (name[^1] == '.' || name[^1] == ' ')
        {
            return PathRepairValidation.Invalid(name[^1] == '.' ? "名称不能以点结尾。" : "名称不能以空格结尾。");
        }

        if (LongPathRules.IsReservedName(name))
        {
            return PathRepairValidation.Invalid($"“{name}”是 Windows 保留名称。");
        }

        int length = parentFolderDisplayPath.Length + 1 + name.Length;
        if (length > TargetPathLength)
        {
            return PathRepairValidation.Invalid($"修复后仍超长（{length} / 目标 {TargetPathLength}）。");
        }

        if (isNameTaken is not null && isNameTaken(name))
        {
            return PathRepairValidation.Invalid($"当前位置已存在名为“{name}”的项目。");
        }

        return PathRepairValidation.Valid(length);
    }
}

/// <summary>建议名称的实时校验结果</summary>
public readonly struct PathRepairValidation
{
    private PathRepairValidation(bool isValid, string error, int length)
    {
        IsValid = isValid;
        Error = error;
        Length = length;
    }

    public bool IsValid { get; }

    /// <summary>不合法时的中文原因；合法时为空串</summary>
    public string Error { get; }

    /// <summary>修复后的完整路径长度</summary>
    public int Length { get; }

    public static PathRepairValidation Valid(int length) => new(true, string.Empty, length);

    public static PathRepairValidation Invalid(string error) => new(false, error, 0);
}
