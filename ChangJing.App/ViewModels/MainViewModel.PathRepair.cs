using ChangJing.App.Core.LongPath;
using ChangJing.App.Core.Operations;
using ChangJing.App.Core.PathRepair;
using CommunityToolkit.Mvvm.Input;
using CustomWin.Utils.Logs;

namespace ChangJing.App.ViewModels;

/// <summary>MainViewModel 的「修复路径」部分</summary>
public sealed partial class MainViewModel : IPathRepairHost
{
    /// <summary>「移动到较短位置」默认目标文件夹名</summary>
    public const string RepairDefaultFolderName = "畅径修复";

    /// <summary>请求界面弹出「修复路径」对话框</summary>
    public event EventHandler<PathRepairRequestEventArgs>? PathRepairRequested;

    /// <summary>「移动到较短位置」的默认目标</summary>
    public string DefaultRepairMoveTargetPath
    {
        get
        {
            string desktop = GetDesktopDisplayPath() ?? _currentLocation;
            return PathHelper.Combine(desktop, RepairDefaultFolderName);
        }
    }

    public bool CanRepairPaths() => !IsThisPcView && !IsRecycleBinView;

    /// <summary>「修复路径…」</summary>
    [RelayCommand(CanExecute = nameof(CanRepairPaths))]
    private void RepairPaths() => RequestPathRepair(_selectedItems.Select(item => item.DisplayPath).ToList());

    /// <summary>用给定的条目集合发起「修复路径」</summary>
    public void RequestPathRepair(IReadOnlyList<string> selectedDisplayPaths)
    {
        if (!CanRepairPaths())
        {
            return;
        }

        // 有选中项：分析选中项本身；无选中项：递归扫描当前文件夹
        var selected = selectedDisplayPaths.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
        string scanFolder = selected.Count == 0 ? _currentLocation : string.Empty;

        if (selected.Count == 0 && (IsThisPcView || string.IsNullOrEmpty(_currentLocation)))
        {
            ShowMessage("无法修复路径", "请先打开一个文件夹，或选中要修复的项目。");
            return;
        }

        var args = new PathRepairRequestEventArgs(selected, scanFolder);
        var handler = PathRepairRequested;

        if (handler is null)
        {
            // 无界面：直接不处理
            return;
        }

        handler(this, args);
    }

    // IPathRepairHost

    /// <summary>执行一批改名；整批作为一个历史项</summary>
    public PathRepairExecutionResult ExecuteRepairRenames(PathRepairPlan plan)
    {
        if (plan is null || (plan.Items.Count == 0 && plan.FolderRenames.Count == 0))
        {
            return PathRepairExecutionResult.Rejected("没有可修复的项目。");
        }

        var entries = new List<HistoryEntry>();
        var folderMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<PathRepairFailure>();
        int repaired = 0;

        // 先缩短上级文件夹，由深到浅：外层先改会让内层父路径失效
        foreach (PathRepairFolderRename folder in OrderByDepthDeepestFirst(plan.FolderRenames, f => f.OriginalFolderDisplayPath))
        {
            try
            {
                string parent = ResolveParent(PathHelper.GetParent(folder.OriginalFolderDisplayPath) ?? string.Empty, folderMapping);
                string originalPath = PathHelper.Combine(parent, PathHelper.GetName(folder.OriginalFolderDisplayPath));
                string oldLong = PathHelper.ToLong(originalPath);
                FileOperationResult result = FileOperations.Rename(oldLong, folder.NewFolderName);

                if (!result.Success)
                {
                    failures.Add(new PathRepairFailure(originalPath, result.Error));
                    LogManager.Warning(
                        typeof(MainViewModel),
                        $"修复路径：缩短上级文件夹失败 {originalPath} → {folder.NewFolderName}：{result.Error}");
                    continue;
                }

                // 记录「原名 → 新名」映射，并把该文件夹之下已有映射一并改写
                RegisterFolderRename(folderMapping, originalPath, result.DisplayPath);
                if (!string.Equals(folder.OriginalFolderDisplayPath, originalPath, StringComparison.OrdinalIgnoreCase))
                {
                    folderMapping[folder.OriginalFolderDisplayPath] = result.DisplayPath;
                }

                entries.Add(new RenameHistoryEntry(originalPath, result.DisplayPath, GetLastWriteUtcOrMin(result.LongPath)));
            }
            catch (Exception ex)
            {
                failures.Add(new PathRepairFailure(folder.OriginalFolderDisplayPath, ex.Message));
                LogManager.Warning(typeof(MainViewModel), $"修复路径：缩短上级文件夹异常：{ex.Message}");
            }
        }

        // 逐项改名，按路径深度由深到浅：先改上层文件夹会让下层子项的父路径失效
        foreach (PathRepairPlanItem item in OrderByDepthDeepestFirst(plan.Items, i => i.OriginalDisplayPath))
        {
            string parent = ResolveParent(item.ParentDisplayPath, folderMapping);
            string originalPath = PathHelper.Combine(parent, item.OriginalName);

            try
            {
                string originalLong = PathHelper.ToLong(originalPath);
                if (!FileOperations.TryGetEntryInfo(originalLong, out _, out _, out _))
                {
                    // 原条目已不在
                    failures.Add(new PathRepairFailure(originalPath, "找不到该项目（可能已被移动或删除）。"));
                    continue;
                }

                DateTime lastWriteUtc = GetLastWriteUtcOrMin(originalLong);
                FileOperationResult result = FileOperations.Rename(originalLong, item.NewName);

                if (!result.Success)
                {
                    failures.Add(new PathRepairFailure(originalPath, result.Error));
                    LogManager.Warning(
                        typeof(MainViewModel),
                        $"修复路径：改名失败 {originalPath} → {item.NewName}：{result.Error}");
                    continue;
                }

                repaired++;

                // 把「原路径 → 新路径」加入映射，让位于该项之下的深层项能解析到
                RegisterFolderRename(folderMapping, item.OriginalDisplayPath, result.DisplayPath);
                entries.Add(new RenameHistoryEntry(originalPath, result.DisplayPath, lastWriteUtc));
            }
            catch (Exception ex)
            {
                failures.Add(new PathRepairFailure(originalPath, ex.Message));
                LogManager.Warning(typeof(MainViewModel), $"修复路径：改名异常：{ex.Message}");
            }
        }

        // 整批作为一个历史项
        if (entries.Count > 0)
        {
            _history.Push(entries.Count == 1 ? entries[0] : new BatchHistoryEntry(OperationHistoryKind.Rename, entries));
            RaiseHistoryChanged();
        }

        if (!IsThisPcView && !IsRecycleBinView)
        {
            Refresh();
        }

        string message = failures.Count == 0
            ? $"已修复 {repaired} 项，现在可以在资源管理器中打开。"
            : $"已修复 {repaired} 项，{failures.Count} 项失败。";

        if (repaired == 0 && failures.Count == 0)
        {
            message = "没有需要修复的项目。";
        }

        string? openFolder = ResolveOpenFolder(plan, folderMapping);

        return new PathRepairExecutionResult(true, repaired, failures.Count, message, moved: false, openFolder, failures);
    }

    /// <summary>把问题项移动到较短位置</summary>
    public PathRepairExecutionResult ExecuteRepairMove(
        IReadOnlyList<string> sourceDisplayPaths,
        string targetFolderDisplayPath)
    {
        if (sourceDisplayPaths.Count == 0)
        {
            return PathRepairExecutionResult.Rejected("没有要移动的项目。");
        }

        if (string.IsNullOrWhiteSpace(targetFolderDisplayPath))
        {
            return PathRepairExecutionResult.Rejected("请先选择要移动到的位置。");
        }

        string target = targetFolderDisplayPath.Trim();

        // 目标不存在则创建
        if (!DirectoryExists(target))
        {
            FileOperationResult created = FileOperations.CreateDirectory(PathHelper.GetParent(PathHelper.ToLong(target)) is { } parent
                ? PathHelper.ToDisplay(parent)
                : target, PathHelper.GetName(target));

            if (!created.Success && !DirectoryExists(target))
            {
                return PathRepairExecutionResult.Rejected($"无法创建目标文件夹：{created.Error}");
            }
        }

        var history = new OperationHistoryContext(OperationHistoryKind.Move, target, sourceDisplayPaths.ToList());
        FileTransferResult result = _coordinator.Move(sourceDisplayPaths, target, history);

        if (!result.Started)
        {
            return PathRepairExecutionResult.Rejected(result.Message);
        }

        return new PathRepairExecutionResult(
            true,
            sourceDisplayPaths.Count,
            0,
            $"正在把 {sourceDisplayPaths.Count} 项移动到「{target}」，完成后即可在资源管理器中打开。",
            moved: true,
            openFolderDisplayPath: target);
    }

    // 内部辅助

    // 由深到浅排序，同深度保持原序
    private static IEnumerable<T> OrderByDepthDeepestFirst<T>(IEnumerable<T> source, Func<T, string> pathSelector)
        => source
            .Select((item, index) => (item, index))
            .OrderByDescending(pair => PathDepth(pathSelector(pair.item)))
            .ThenBy(pair => pair.index)
            .Select(pair => pair.item);

    private static int PathDepth(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return 0;
        }

        int depth = 0;
        foreach (char c in path)
        {
            if (c == '\\')
            {
                depth++;
            }
        }

        return depth;
    }

    // 上级文件夹改名后，其下已登记的路径要跟着换前缀
    private static void RegisterFolderRename(
        Dictionary<string, string> mapping,
        string originalPath,
        string newPath)
    {
        var updates = new List<KeyValuePair<string, string>>();

        foreach ((string key, string value) in mapping)
        {
            string prefix = originalPath.EndsWith('\\') ? originalPath : originalPath + "\\";
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                updates.Add(new KeyValuePair<string, string>(key, newPath + value.Substring(originalPath.Length)));
            }
        }

        foreach (KeyValuePair<string, string> update in updates)
        {
            mapping[update.Key] = update.Value;
        }

        mapping[originalPath] = newPath;
    }

    private static string ResolveParent(string parentDisplayPath, IReadOnlyDictionary<string, string> folderMapping)
    {
        if (folderMapping.Count == 0)
        {
            return parentDisplayPath;
        }

        // 直接命中，或父目录位于某个被改名的文件夹之下
        foreach ((string oldFolder, string newFolder) in folderMapping)
        {
            if (string.Equals(parentDisplayPath, oldFolder, StringComparison.OrdinalIgnoreCase))
            {
                return newFolder;
            }

            string prefix = oldFolder.EndsWith('\\') ? oldFolder : oldFolder + "\\";
            if (parentDisplayPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return newFolder + parentDisplayPath.Substring(oldFolder.Length);
            }
        }

        return parentDisplayPath;
    }

    private static string? ResolveOpenFolder(PathRepairPlan plan, IReadOnlyDictionary<string, string> folderMapping)
    {
        if (plan.Items.Count > 0)
        {
            string parent = ResolveParent(plan.Items[0].ParentDisplayPath, folderMapping);
            return string.IsNullOrEmpty(parent) ? null : parent;
        }

        if (plan.FolderRenames.Count > 0)
        {
            return folderMapping.TryGetValue(plan.FolderRenames[0].OriginalFolderDisplayPath, out string? mapped)
                ? mapped
                : PathHelper.GetParent(plan.FolderRenames[0].OriginalFolderDisplayPath);
        }

        return null;
    }

    private static DateTime GetLastWriteUtcOrMin(string longPath)
        => FileOperations.TryGetEntryInfo(longPath, out _, out _, out DateTime utc) ? utc : DateTime.MinValue;

    private static bool DirectoryExists(string displayPath)
    {
        try
        {
            return FileOperations.TryGetEntryInfo(
                PathHelper.ToLong(displayPath),
                out uint attributes,
                out _,
                out _)
                && (attributes & Core.FileSystem.NativeMethods.FileAttributeDirectory) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? GetDesktopDisplayPath()
    {
        try
        {
            foreach (Core.FileSystem.KnownFolderItem folder in Core.FileSystem.LocationProvider.GetKnownFolders())
            {
                if (folder.Id == "Desktop" && folder.IsAvailable && !string.IsNullOrEmpty(folder.DisplayPath))
                {
                    return folder.DisplayPath;
                }
            }
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(MainViewModel), $"定位桌面文件夹失败：{ex.Message}");
        }

        return null;
    }
}
