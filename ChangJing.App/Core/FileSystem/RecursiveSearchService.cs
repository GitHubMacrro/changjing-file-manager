using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using ChangJing.App.Core.LongPath;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Core.FileSystem;

/// <summary>递归搜索服务：遍历子文件夹，把命中的条目分批回调</summary>
public sealed class RecursiveSearchService
{
    /// <summary>默认批量回调大小，单位条</summary>
    public const int DefaultBatchSize = 200;

    /// <summary>进度回调的最小间隔，单位毫秒</summary>
    private const int ProgressIntervalMilliseconds = 200;

    /// <summary>每隔多少条检查一次取消</summary>
    private const int CancellationCheckInterval = 256;

    /// <summary>正则匹配超时，防止病态模式长时间回溯</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    /// <summary>批量回调大小，单位条</summary>
    public int BatchSize { get; }

    /// <summary>是否不把隐藏 / 系统条目作为结果回调，不影响遍历</summary>
    public bool SkipHiddenAndSystem { get; }

    public RecursiveSearchService(int batchSize = DefaultBatchSize, bool skipHiddenAndSystem = false)
    {
        BatchSize = batchSize > 0 ? batchSize : DefaultBatchSize;
        SkipHiddenAndSystem = skipHiddenAndSystem;
    }

    /// <summary>关键字是否含通配符 * 或 ?</summary>
    public static bool IsWildcardPattern(string keyword)
        => !string.IsNullOrEmpty(keyword)
           && (keyword.IndexOf('*') >= 0 || keyword.IndexOf('?') >= 0);

    /// <summary>文件名是否匹配关键字：支持 * / ? 通配符，否则子串包含</summary>
    public static bool NameMatches(string name, string keywordPattern)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(keywordPattern))
        {
            return false;
        }

        return NameMatcher.Create(keywordPattern).IsMatch(name);
    }

    /// <summary>递归搜索根目录及其所有子文件夹；不抛异常</summary>
    public SearchSummary Search(
        string rootDisplayPath,
        string keyword,
        Action<IReadOnlyList<SearchHit>> onBatch,
        Action<SearchProgress>? onProgress,
        CancellationToken cancellationToken = default,
        SearchPauseToken? pauseToken = null)
    {
        ArgumentNullException.ThrowIfNull(onBatch);

        int matched = 0;
        int scanned = 0;
        int skippedDirectories = 0;
        int skippedReparsePoints = 0;
        bool canceled = false;
        string? error = null;

        var batch = new List<SearchHit>(BatchSize);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        DateTime lastProgressUtc = DateTime.MinValue;
        string currentDirectoryDisplay = PathHelper.ToDisplay(rootDisplayPath);
        string rootLong = string.Empty;

        // 交回调后换新列表，回调方持有的批次不会被改写
        void FlushBatch()
        {
            if (batch.Count == 0)
            {
                return;
            }

            List<SearchHit> handedOff = batch;
            batch = new List<SearchHit>(BatchSize);
            onBatch(handedOff);
        }

        // force 为 true 时无视间隔
        void ReportProgress(bool force)
        {
            if (onProgress is null)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (!force && (now - lastProgressUtc).TotalMilliseconds < ProgressIntervalMilliseconds)
            {
                return;
            }

            lastProgressUtc = now;
            onProgress(new SearchProgress(matched, scanned, skippedDirectories, currentDirectoryDisplay));
        }

        try
        {
            NameMatcher matcher = NameMatcher.Create(keyword);

            try
            {
                rootLong = PathHelper.ToLong(rootDisplayPath);
                currentDirectoryDisplay = PathHelper.ToDisplay(rootLong);
            }
            catch (ArgumentException ex)
            {
                LogManager.Debug(typeof(RecursiveSearchService), $"搜索根路径不可用：{rootDisplayPath} — {ex.Message}");
                ReportProgress(force: true);
                return new SearchSummary(0, 0, 0, 0, canceled: false, error: ex.Message);
            }

            stack.Push(rootLong);
            bool isRoot = true;

            ReportProgress(force: true);

            while (stack.Count > 0)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    canceled = true;
                    break;
                }

                // 暂停时在此等待，继续后从原处接着遍历
                pauseToken?.WaitIfPaused(cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    canceled = true;
                    break;
                }

                string directoryLong = stack.Pop();
                bool isRootDirectory = isRoot;
                isRoot = false;

                // 防环兜底：万一某个目录被重复压栈也不会跑第二遍
                if (!visited.Add(directoryLong))
                {
                    continue;
                }

                currentDirectoryDisplay = PathHelper.ToDisplay(directoryLong);

                string searchRoot;
                string searchPattern;
                try
                {
                    searchRoot = PathHelper.EnsureTrailingSeparator(directoryLong);
                    searchPattern = searchRoot + "*";
                }
                catch (ArgumentException ex)
                {
                    skippedDirectories++;
                    LogManager.Debug(typeof(RecursiveSearchService), $"跳过无效目录 {directoryLong}：{ex.Message}");
                    continue;
                }

                SafeFindHandle handle = NativeMethods.FindFirstFileExW(
                    searchPattern,
                    NativeMethods.FindExInfoBasic,
                    out NativeMethods.WIN32_FIND_DATAW findData,
                    NativeMethods.FindExSearchNameMatch,
                    IntPtr.Zero,
                    NativeMethods.FindFirstExLargeFetch);

                if (handle.IsInvalid)
                {
                    int openError = Marshal.GetLastWin32Error();
                    handle.Dispose();

                    skippedDirectories++;
                    if (isRootDirectory)
                    {
                        error = Win32ErrorMessages.Describe(openError);
                    }

                    // 无权限 / 打不开：用 Debug，Warning 会淹没日志
                    LogManager.Debug(
                        typeof(RecursiveSearchService),
                        $"无法枚举目录 {currentDirectoryDisplay}：{Win32ErrorMessages.Describe(openError)}（错误码 {openError}）");

                    ReportProgress(force: false);
                    continue;
                }

                string folderDisplay = currentDirectoryDisplay;

                try
                {
                    int sinceCancellationCheck = 0;

                    while (true)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            canceled = true;
                            break;
                        }

                        string name = findData.cFileName;
                        if (!string.IsNullOrEmpty(name) && name != "." && name != "..")
                        {
                            scanned++;

                            uint attributes = findData.dwFileAttributes;
                            bool isDirectory = (attributes & NativeMethods.FileAttributeDirectory) != 0;
                            bool isReparsePoint = (attributes & NativeMethods.FileAttributeReparsePoint) != 0;

                            // 重解析点匹配照常，但绝不压栈
                            if (isDirectory)
                            {
                                if (isReparsePoint)
                                {
                                    skippedReparsePoints++;
                                }
                                else
                                {
                                    stack.Push(searchRoot + name);
                                }
                            }

                            bool hiddenOrSystem =
                                (attributes & NativeMethods.FileAttributeHidden) != 0
                                || (attributes & NativeMethods.FileAttributeSystem) != 0;

                            if (matcher.IsMatch(name) && !(SkipHiddenAndSystem && hiddenOrSystem))
                            {
                                string childLong = searchRoot + name;
                                matched++;
                                batch.Add(new SearchHit(
                                    name,
                                    childLong,
                                    PathHelper.ToDisplay(childLong),
                                    folderDisplay,
                                    directoryLong,
                                    attributes,
                                    isDirectory,
                                    isDirectory ? 0UL : findData.Size,
                                    findData.LastWriteTimeUtc));
                            }
                        }

                        if (batch.Count >= BatchSize)
                        {
                            FlushBatch();
                        }

                        if (++sinceCancellationCheck >= CancellationCheckInterval)
                        {
                            sinceCancellationCheck = 0;
                            ReportProgress(force: false);

                            if (cancellationToken.IsCancellationRequested)
                            {
                                canceled = true;
                                break;
                            }

                            // 暂停时在此等待，不丢结果
                            pauseToken?.WaitIfPaused(cancellationToken);
                        }

                        if (!NativeMethods.FindNextFileW(handle, out findData))
                        {
                            int nextError = Marshal.GetLastWin32Error();
                            if (!Win32ErrorMessages.IsEndOfEnumeration(nextError))
                            {
                                // 中途失败：保留已收集结果，继续兄弟目录
                                skippedDirectories++;
                                LogManager.Debug(
                                    typeof(RecursiveSearchService),
                                    $"枚举中断 {currentDirectoryDisplay}：{Win32ErrorMessages.Describe(nextError)}（错误码 {nextError}）");
                            }

                            break;
                        }
                    }
                }
                finally
                {
                    handle.Dispose();
                }

                if (canceled)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(RecursiveSearchService), ex);
            error ??= "搜索时发生内部错误，详细信息已写入日志。";
        }

        // 取消与正常结束都要冲刷最后一批
        try
        {
            FlushBatch();
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(RecursiveSearchService), ex);
            error ??= "处理搜索结果时发生内部错误，详细信息已写入日志。";
        }

        ReportProgress(force: true);

        return new SearchSummary(matched, scanned, skippedDirectories, skippedReparsePoints, canceled, error);
    }

    /// <summary>预编译的名称匹配器：关键字一次性转成 Regex 或普通字符串</summary>
    private sealed class NameMatcher
    {
        private readonly Regex? _wildcardRegex;
        private readonly string? _literal;

        private NameMatcher(Regex? wildcardRegex, string? literal)
        {
            _wildcardRegex = wildcardRegex;
            _literal = literal;
        }

        /// <summary>含通配符走正则，否则走子串包含</summary>
        public static NameMatcher Create(string keyword)
        {
            if (IsWildcardPattern(keyword))
            {
                return new NameMatcher(BuildWildcardRegex(keyword), literal: null);
            }

            return new NameMatcher(wildcardRegex: null, literal: keyword);
        }

        public bool IsMatch(string name)
        {
            if (_literal is not null)
            {
                return name.Contains(_literal, StringComparison.CurrentCultureIgnoreCase);
            }

            try
            {
                return _wildcardRegex!.IsMatch(name);
            }
            catch (RegexMatchTimeoutException ex)
            {
                LogManager.Debug(typeof(RecursiveSearchService), $"名称匹配超时（{name}）：{ex.Message}");
                return false;
            }
        }

        /// <summary>把 * / ? 通配符模式转成锚定的正则</summary>
        private static Regex BuildWildcardRegex(string pattern)
        {
            var builder = new StringBuilder(pattern.Length + 8);
            builder.Append(@"\A");

            int literalStart = 0;
            for (int index = 0; index < pattern.Length; index++)
            {
                char c = pattern[index];
                if (c != '*' && c != '?')
                {
                    continue;
                }

                if (index > literalStart)
                {
                    builder.Append(Regex.Escape(pattern.Substring(literalStart, index - literalStart)));
                }

                builder.Append(c == '*' ? ".*" : ".");
                literalStart = index + 1;
            }

            if (literalStart < pattern.Length)
            {
                builder.Append(Regex.Escape(pattern.Substring(literalStart)));
            }

            builder.Append(@"\z");

            return new Regex(
                builder.ToString(),
                RegexOptions.IgnoreCase | RegexOptions.Singleline,
                RegexTimeout);
        }
    }
}
