using ChangJing.App.Core.LongPath;

namespace ChangJing.App.Core.Operations;

/// <summary>新建 / 重命名时名称合法性的判定结果</summary>
public readonly struct NameValidationResult
{
    private NameValidationResult(bool isValid, string error, string warning)
    {
        IsValid = isValid;
        Error = error;
        Warning = warning;
    }

    public bool IsValid { get; }

    /// <summary>阻断性错误，没有时为空串</summary>
    public string Error { get; }

    /// <summary>非阻断性提醒，没有时为空串</summary>
    public string Warning { get; }

    public bool HasError => Error.Length > 0;

    public bool HasWarning => Warning.Length > 0;

    public static NameValidationResult Ok(string warning = "")
        => new(true, string.Empty, warning);

    public static NameValidationResult Failed(string error)
        => new(false, error, string.Empty);
}

/// <summary>新建 / 重命名名称校验</summary>
// 不访问磁盘，重名由调用方通过回调判定
public static class NameValidator
{
    private static readonly char[] IllegalNameChars = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    /// <summary>名称长度上限，NTFS 单段 255 字符</summary>
    public const int MaxNameLength = 255;

    public static NameValidationResult Validate(
        string? newName,
        string parentDisplayPath,
        Func<string, bool>? isNameTaken = null)
    {
        string name = newName ?? string.Empty;

        if (name.Trim().Length == 0)
        {
            return NameValidationResult.Failed("名称不能为空。");
        }

        if (name.Length > MaxNameLength)
        {
            return NameValidationResult.Failed($"名称过长（{name.Length} / 上限 {MaxNameLength} 个字符）。");
        }

        foreach (char c in name)
        {
            if (c < ' ')
            {
                return NameValidationResult.Failed("名称不能包含控制字符。");
            }

            if (Array.IndexOf(IllegalNameChars, c) >= 0)
            {
                return NameValidationResult.Failed($"名称不能包含字符“{c}”。");
            }
        }

        if (name[^1] == '.' || name[^1] == ' ')
        {
            return NameValidationResult.Failed(
                name[^1] == '.' ? "名称不能以点（.）结尾。" : "名称不能以空格结尾。");
        }

        if (LongPathRules.IsReservedName(name))
        {
            return NameValidationResult.Failed($"“{name}”是 Windows 保留名称，不能使用。");
        }

        if (isNameTaken is not null && isNameTaken(name))
        {
            return NameValidationResult.Failed($"当前位置已存在名为“{name}”的项目。");
        }

        // 新完整路径过长只提醒，本工具支持长路径
        string fullDisplayPath = parentDisplayPath.TrimEnd('\\', '/') + "\\" + name;
        if (fullDisplayPath.Length > LongPathRules.MaxExplorerPathLength)
        {
            return NameValidationResult.Ok(
                $"提醒：新路径长度 {fullDisplayPath.Length} 字符，已超出资源管理器上限" +
                $"（{LongPathRules.MaxExplorerPathLength}）。本工具可以正常使用。");
        }

        return NameValidationResult.Ok();
    }
}
