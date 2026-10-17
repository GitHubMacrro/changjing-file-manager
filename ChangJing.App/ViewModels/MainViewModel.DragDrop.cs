using ChangJing.App.Core.DragDrop;
using ChangJing.App.Core.LongPath;
using ChangJing.App.Core.Operations;
using ChangJing.App.Services;
using CustomWin.Utils.Logs;

namespace ChangJing.App.ViewModels;

/// <summary>MainViewModel 的拖放部分：计算拖放效果、执行内部拖放与外部拖入</summary>
public sealed partial class MainViewModel
{
    /// <summary>计算一次拖放的效果</summary>
    public DropDecision ResolveDrop(
        IReadOnlyList<string> sourceDisplayPaths,
        string targetFolderDisplayPath,
        bool ctrl,
        bool shift,
        bool alt,
        bool forceCrossVolume = false)
    {
        string name = DescribeFolderName(targetFolderDisplayPath);
        return DropRules.Decide(sourceDisplayPaths, targetFolderDisplayPath, name, ctrl, shift, alt,
            sameVolume: null, forceCrossVolume);
    }

    /// <summary>执行一次内部 / 外部拖放</summary>
    public string PerformDrop(
        IReadOnlyList<string> sourceDisplayPaths,
        string targetFolderDisplayPath,
        bool ctrl,
        bool shift,
        bool alt,
        bool forceCrossVolume = false)
    {
        if (sourceDisplayPaths is null || sourceDisplayPaths.Count == 0)
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(targetFolderDisplayPath))
        {
            return "无法放入：目标文件夹无效。";
        }

        // 回收站页 / 搜索结果页不可作为放置目标
        DropDecision decision = ResolveDrop(sourceDisplayPaths, targetFolderDisplayPath, ctrl, shift, alt, forceCrossVolume);
        if (decision.Forbidden)
        {
            return decision.Tooltip;
        }

        return decision.Action switch
        {
            DropAction.Move => ExecuteDropTransfer(OperationHistoryKind.Move, sourceDisplayPaths, targetFolderDisplayPath),
            DropAction.Copy => ExecuteDropTransfer(OperationHistoryKind.Copy, sourceDisplayPaths, targetFolderDisplayPath),
            DropAction.Shortcut => ExecuteDropShortcut(sourceDisplayPaths, targetFolderDisplayPath),
            _ => string.Empty,
        };
    }

    private string ExecuteDropTransfer(
        OperationHistoryKind kind,
        IReadOnlyList<string> sourceDisplayPaths,
        string targetFolderDisplayPath)
    {
        // 一次拖放 = 一步撤销（多条目合并为一个批量历史项）
        var history = new OperationHistoryContext(kind, targetFolderDisplayPath, sourceDisplayPaths);

        FileTransferResult result = kind == OperationHistoryKind.Move
            ? _coordinator.Move(sourceDisplayPaths, targetFolderDisplayPath, history)
            : _coordinator.Copy(sourceDisplayPaths, targetFolderDisplayPath, history);

        if (!result.Started)
        {
            ShowMessage(kind == OperationHistoryKind.Move ? "无法移动" : "无法复制", result.Message);
        }

        StatusText = result.Message;
        return result.Message;
    }

    private string ExecuteDropShortcut(IReadOnlyList<string> sourceDisplayPaths, string targetFolderDisplayPath)
    {
        int created = 0;
        var failures = new List<string>();

        foreach (string source in sourceDisplayPaths)
        {
            if (ShortcutCreator.TryCreate(targetFolderDisplayPath, source, out string? createdPath, out string error)
                && !string.IsNullOrEmpty(createdPath))
            {
                created++;
                PushCreateHistory(createdPath!, isDirectory: false);
            }
            else
            {
                failures.Add($"{PathHelper.GetName(source)}：{error}");
            }
        }

        if (created == 0)
        {
            string reason = failures.Count > 0 ? string.Join("；", failures) : "没有创建任何快捷方式。";
            ShowMessage("无法创建快捷方式", reason);
            StatusText = reason;
            return reason;
        }

        string message = $"已在 {DescribeFolderName(targetFolderDisplayPath)} 创建 {created} 个链接。";
        if (failures.Count > 0)
        {
            message += $"失败 {failures.Count} 个。";
        }

        StatusText = message;

        // 创建完刷新列表，让新快捷方式出现
        if (!IsThisPcView && !IsRecycleBinView)
        {
            Refresh();
        }

        return message;
    }

    private static string DescribeFolderName(string targetFolderDisplayPath)
    {
        try
        {
            string display = PathHelper.ToDisplay(targetFolderDisplayPath);
            if (display.Length <= 3 && display.EndsWith('\\'))
            {
                return display;
            }

            string name = PathHelper.GetName(display);
            return string.IsNullOrEmpty(name) ? display : name;
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(MainViewModel), $"取文件夹显示名失败：{ex.Message}");
            return targetFolderDisplayPath;
        }
    }

    // 放置目标可用性

    /// <summary>当前是否允许拖放</summary>
    public bool CanAcceptDrop => !IsRecycleBinView && !IsSearchResultView;

    /// <summary>把目标路径规范成可用的放置目录；不可用时返回 null</summary>
    public string? ResolveDropTarget(string? targetFolderDisplayPath)
    {
        if (!CanAcceptDrop || string.IsNullOrWhiteSpace(targetFolderDisplayPath))
        {
            return null;
        }

        string display = PathHelper.ToDisplay(targetFolderDisplayPath!);
        return display;
    }
}
