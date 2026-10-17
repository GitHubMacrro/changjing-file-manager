using ChangJing.App.Core.FileSystem;
using ChangJing.App.Core.LongPath;
using ChangJing.App.Core.Operations;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Core.PathRepair;

/// <summary>「修复路径」问题扫描：递归扫描文件夹，或分析一组已选中条目</summary>
public static class PathRepairScanner
{
    /// <summary>扫描时的批量回调大小</summary>
    public const int BatchSize = 200;

    /// <summary>递归扫描根目录及子文件夹，找出无法处理的条目</summary>
    public static PathRepairScanResult ScanRecursive(
        string rootDisplayPath,
        Action<IReadOnlyList<PathRepairCandidate>> onBatch,
        Action<SearchProgress>? onProgress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onBatch);

        int problemCount = 0;
        var pending = new List<PathRepairCandidate>(BatchSize);

        void Flush()
        {
            if (pending.Count == 0)
            {
                return;
            }

            List<PathRepairCandidate> handedOff = pending;
            pending = new List<PathRepairCandidate>(BatchSize);
            onBatch(handedOff);
        }

        // 用「*」通配符匹配所有名称，借用搜索服务的遍历骨架
        var search = new RecursiveSearchService(BatchSize);

        SearchSummary summary = search.Search(
            rootDisplayPath,
            "*",
            hits =>
            {
                foreach (SearchHit hit in hits)
                {
                    PathRepairCandidate? candidate = BuildCandidate(
                        hit.Name, hit.DisplayPath, hit.LongPath, hit.FolderDisplayPath, hit.IsDirectory);

                    if (candidate is null)
                    {
                        continue;
                    }

                    problemCount++;
                    pending.Add(candidate);
                    if (pending.Count >= BatchSize)
                    {
                        Flush();
                    }
                }
            },
            onProgress,
            cancellationToken);

        Flush();

        return new PathRepairScanResult(
            problemCount,
            summary.ScannedCount,
            summary.SkippedDirectoryCount,
            summary.SkippedReparsePointCount,
            summary.Canceled,
            summary.Error);
    }

    /// <summary>分析一组已选中条目，返回其中的问题项</summary>
    public static IReadOnlyList<PathRepairCandidate> AnalyzePaths(IReadOnlyList<string> displayPaths)
    {
        var results = new List<PathRepairCandidate>();

        foreach (string displayPath in displayPaths)
        {
            if (string.IsNullOrWhiteSpace(displayPath))
            {
                continue;
            }

            string longPath;
            bool isDirectory = false;
            try
            {
                longPath = PathHelper.ToLong(displayPath);
                if (FileOperations.TryGetEntryInfo(longPath, out uint attributes, out _, out _))
                {
                    isDirectory = (attributes & NativeMethods.FileAttributeDirectory) != 0;
                }
            }
            catch (Exception ex)
            {
                LogManager.Warning(typeof(PathRepairScanner), $"分析路径失败（{displayPath}）：{ex.Message}");
                continue;
            }

            string name = PathHelper.GetName(displayPath);
            string parent = PathHelper.ToDisplay(PathHelper.GetParent(longPath) ?? string.Empty);

            PathRepairCandidate? candidate = BuildCandidate(name, displayPath, longPath, parent, isDirectory);
            if (candidate is not null)
            {
                results.Add(candidate);
            }
        }

        return results;
    }

    /// <summary>构造一条问题项；无问题时返回 null</summary>
    public static PathRepairCandidate? BuildCandidate(
        string name,
        string displayPath,
        string longPath,
        string parentFolderDisplayPath,
        bool isDirectory)
    {
        PathRepairIssueKind issues = PathRepairRules.DetectIssues(name, displayPath);
        if (issues == PathRepairIssueKind.None)
        {
            return null;
        }

        PathRepairSuggestion suggestion = PathRepairRules.Suggest(name, parentFolderDisplayPath, isDirectory);

        return new PathRepairCandidate(
            name,
            displayPath,
            longPath,
            parentFolderDisplayPath,
            isDirectory,
            issues,
            PathRepairRules.DescribeIssues(issues),
            displayPath.Length,
            suggestion);
    }
}
