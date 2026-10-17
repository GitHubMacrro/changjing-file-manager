using ChangJing.App.Core.LongPath;
using ChangJing.App.Core.Operations;

namespace ChangJing.App.Core.DragDrop;

public enum DropAction
{
    None,

    Move,

    Copy,

    /// <summary>在目标文件夹创建指向源的快捷方式</summary>
    Shortcut,
}

public sealed class DropDecision
{
    private DropDecision(DropAction action, bool forbidden, string tooltip)
    {
        Action = action;
        Forbidden = forbidden;
        Tooltip = tooltip;
    }

    public DropAction Action { get; }

    public bool Forbidden { get; }

    public string Tooltip { get; }

    public bool IsValid => !Forbidden && Action != DropAction.None;

    internal static DropDecision Forbid(string tooltip) => new(DropAction.None, true, tooltip);

    internal static DropDecision Allow(DropAction action, string tooltip) => new(action, false, tooltip);
}

/// <summary>拖放的纯逻辑：默认效果、修饰键、禁止落点判定与提示文案</summary>
public static class DropRules
{
    /// <summary>按同盘 / 跨盘 + 修饰键决定默认效果</summary>
    public static DropAction ResolveDefaultEffect(bool sameVolume, bool ctrl, bool shift, bool alt)
    {
        // 修饰键顺序与资源管理器一致：Ctrl > Shift > Alt
        if (ctrl)
        {
            return DropAction.Copy;
        }

        if (shift)
        {
            return DropAction.Move;
        }

        if (alt)
        {
            return DropAction.Shortcut;
        }

        return sameVolume ? DropAction.Move : DropAction.Copy;
    }

    /// <summary>是否为禁止落点：目标等于源，或目标是源的子文件夹</summary>
    public static bool IsForbiddenDrop(string sourceDisplayPath, string targetFolderDisplayPath)
    {
        if (string.IsNullOrWhiteSpace(sourceDisplayPath) || string.IsNullOrWhiteSpace(targetFolderDisplayPath))
        {
            return false;
        }

        string source = Normalize(sourceDisplayPath);
        string target = Normalize(targetFolderDisplayPath);

        if (source.Length == 0 || target.Length == 0)
        {
            return false;
        }

        // 落到自身或子文件夹必须禁止
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 外部拖入的可能是相对 / 设备路径，EnsureTrailingSeparator 会抛异常，先判绝对性
        if (!PathHelper.IsAbsolute(source) || !PathHelper.IsAbsolute(target))
        {
            return false;
        }

        string sourceWithSeparator = PathHelper.EnsureTrailingSeparator(source);
        return target.StartsWith(sourceWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAnyForbidden(IEnumerable<string> sourceDisplayPaths, string targetFolderDisplayPath)
        => sourceDisplayPaths.Any(source => IsForbiddenDrop(source, targetFolderDisplayPath));

    /// <summary>把动作转成拖动提示文案</summary>
    public static string Describe(DropAction action, string targetFolderName)
    {
        string name = string.IsNullOrEmpty(targetFolderName) ? "目标文件夹" : targetFolderName;
        return action switch
        {
            DropAction.Move => $"移动到 {name}",
            DropAction.Copy => $"复制到 {name}",
            DropAction.Shortcut => $"在 {name} 创建链接",
            _ => string.Empty,
        };
    }

    /// <summary>综合判定一次拖放：先查禁止落点，再定动作并给出提示</summary>
    public static DropDecision Decide(
        IReadOnlyList<string> sourceDisplayPaths,
        string targetFolderDisplayPath,
        string targetFolderName,
        bool ctrl,
        bool shift,
        bool alt,
        bool? sameVolume = null,
        bool forceCrossVolume = false)
    {
        if (sourceDisplayPaths is null || sourceDisplayPaths.Count == 0 || string.IsNullOrWhiteSpace(targetFolderDisplayPath))
        {
            return DropDecision.Forbid(string.Empty);
        }

        if (IsAnyForbidden(sourceDisplayPaths, targetFolderDisplayPath))
        {
            return DropDecision.Forbid("不能将文件夹移动到自身或其子文件夹中");
        }

        bool same = sameVolume ?? ResolveSameVolume(sourceDisplayPaths, targetFolderDisplayPath);
        if (forceCrossVolume)
        {
            same = false;
        }

        DropAction action = ResolveDefaultEffect(same, ctrl, shift, alt);
        return DropDecision.Allow(action, Describe(action, targetFolderName));
    }

    /// <summary>源与目标是否同卷，取第一项为代表</summary>
    private static bool ResolveSameVolume(IReadOnlyList<string> sources, string targetFolder)
    {
        foreach (string source in sources)
        {
            if (!string.IsNullOrWhiteSpace(source))
            {
                // 外部拖入的可能是相对 / 设备路径，ToLong 会抛异常，用 TryToLong 保护
                if (!PathHelper.TryToLong(source, out string longSource, out _) ||
                    !PathHelper.TryToLong(targetFolder, out string longTarget, out _))
                {
                    return false;
                }

                return TransferVolume.IsSameVolume(longSource, longTarget);
            }
        }

        return false;
    }

    /// <summary>规范化成可比较的普通路径，去掉尾分隔符</summary>
    private static string Normalize(string displayPath)
    {
        try
        {
            string display = PathHelper.ToDisplay(displayPath);
            if (display.Length > 3 && (display.EndsWith('\\') || display.EndsWith('/')))
            {
                display = display.TrimEnd('\\', '/');
            }

            return display;
        }
        catch
        {
            return string.Empty;
        }
    }
}
