using System.Diagnostics;
using System.IO;
using System.Text;
using ChangJing.App.Core.LongPath;
using CustomWin.Utils.Logs;

namespace ChangJing.Regression;

/// <summary>回归 harness 的断言 / 临时目录 / 日志基础设施</summary>
// 测试数据落在 %TEMP%\cj-reg-&lt;guid&gt;，结束统一清理，保留名 / 长路径用 \\?\
public static class H
{
    private static readonly List<string> Failures = new();
    private static int _passed;

    /// <summary>测试工作根目录 <c>%TEMP%\cj-reg-&lt;guid&gt;</c></summary>
    public static string Root { get; } = Path.Combine(
        Path.GetTempPath(), "cj-reg-" + Guid.NewGuid().ToString("N")[..8]);

    public static int Total => _passed + Failures.Count;

    public static int Passed => _passed;

    /// <summary>失败明细，供最终汇总打印</summary>
    public static IReadOnlyList<string> FailureList => Failures;

    /// <summary>日志文件路径 <c>%TEMP%\cj-reg-&lt;guid&gt;\harness-log.txt</c></summary>
    public static string LogPath { get; private set; } = string.Empty;

    private static StreamWriter? _log;

    /// <summary>初始化：建工作目录 + 日志 + 日志服务</summary>
    public static void Initialize()
    {
        Directory.CreateDirectory(Root);
        LogPath = Path.Combine(Root, "harness-log.txt");
        _log = new StreamWriter(LogPath, append: false, Encoding.UTF8) { AutoFlush = true };

        try
        {
            LogManager.LogDirectory = Path.Combine(Root, "logs");
            LogManager.LogFileName = "reg";
            LogManager.PrintLogToConsole = false;
            LogManager.EnableFileLimit = true;
            LogManager.LimitSize = 5;
        }
        catch
        {
            // 日志初始化失败不影响测试
        }

        Info($"工作目录：{Root}");
    }

    /// <summary>在控制台与日志同时写一行信息</summary>
    public static void Info(string message)
    {
        Console.WriteLine(message);
        _log?.WriteLine(message);
    }

    public static void Check(string name, bool condition, string detail = "")
    {
        if (condition)
        {
            _passed++;
            _log?.WriteLine($"  [PASS] {name}");
        }
        else
        {
            string line = string.IsNullOrEmpty(detail) ? name : $"{name} — {detail}";
            Failures.Add(line);
            Console.WriteLine($"  [FAIL] {line}");
            _log?.WriteLine($"  [FAIL] {line}");
        }
    }

    public static void Eq<T>(string name, T expected, T actual)
        => Check(name, EqualityComparer<T>.Default.Equals(expected, actual), $"期望 [{expected}]，实际 [{actual}]");

    public static void NotEq<T>(string name, T notExpected, T actual)
        => Check(name, !EqualityComparer<T>.Default.Equals(notExpected, actual), $"不应等于 [{notExpected}]，实际 [{actual}]");

    /// <summary>断言字符串相等，忽略大小写，用于路径</summary>
    public static void EqPath(string name, string expected, string actual)
        => Check(name, string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase),
            $"期望 [{expected}]，实际 [{actual}]");

    public static void Group(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {title} ===");
        _log?.WriteLine();
        _log?.WriteLine($"=== {title} ===");
    }

    /// <summary>新建一个位于工作根下的唯一子目录，返回普通路径</summary>
    public static string NewDir(string name)
    {
        string dir = Path.Combine(Root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>在工作目录内新建一个唯一测试目录，带随机后缀</summary>
    public static string UniqueDir(string prefix)
        => NewDir(prefix + "-" + Guid.NewGuid().ToString("N")[..6]);

    // 文件系统辅助，一律走 \\?\

    /// <summary>用扩展路径写文件，可安全创建结尾点 / 空格、保留名、超长路径</summary>
    public static void WriteFile(string displayPath, string content)
    {
        string longPath = PathHelper.ToLong(displayPath);
        string? parent = Path.GetDirectoryName(longPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.WriteAllText(longPath, content, new UTF8Encoding(false));
    }

    /// <summary>用扩展路径写二进制文件</summary>
    public static void WriteBytes(string displayPath, byte[] content)
    {
        string longPath = PathHelper.ToLong(displayPath);
        string? parent = Path.GetDirectoryName(longPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.WriteAllBytes(longPath, content);
    }

    /// <summary>用扩展路径建目录</summary>
    public static void MakeDir(string displayPath)
        => Directory.CreateDirectory(PathHelper.ToLong(displayPath));

    /// <summary>用扩展路径读文本</summary>
    public static string ReadFile(string displayPath)
        => File.ReadAllText(PathHelper.ToLong(displayPath));

    /// <summary>用扩展路径判断存在，文件或目录</summary>
    public static bool Exists(string displayPath)
    {
        string longPath = PathHelper.ToLong(displayPath);
        return File.Exists(longPath) || Directory.Exists(longPath);
    }

    /// <summary>构造超长嵌套路径，返回最深层普通路径</summary>
    public static string BuildDeepPath(string root, int depth, int segmentLength = 40)
    {
        string current = root;
        for (int i = 0; i < depth; i++)
        {
            string segment = "d" + i.ToString("D2") + new string('x', Math.Max(0, segmentLength - 3));
            current = Path.Combine(current, segment);
            Directory.CreateDirectory(PathHelper.ToLong(current));
        }

        return current;
    }

    // 清理

    /// <summary>健壮删除一棵树，清属性用扩展路径并重试</summary>
    public static void DeleteTreeRobust(string displayPath)
    {
        try
        {
            string longPath = PathHelper.ToLong(displayPath);
            if (!Directory.Exists(longPath) && !File.Exists(longPath))
            {
                return;
            }

            ClearAttributes(longPath);
            try
            {
                Directory.Delete(longPath, recursive: true);
            }
            catch
            {
                // 二次尝试：逐项清属性后再删
                ClearAttributes(longPath);
                Directory.Delete(longPath, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _log?.WriteLine($"  [cleanup] 删除失败 {displayPath}：{ex.Message}");
        }
    }

    private static void ClearAttributes(string longPath)
    {
        try
        {
            if (File.Exists(longPath))
            {
                File.SetAttributes(longPath, FileAttributes.Normal);
                return;
            }

            if (!Directory.Exists(longPath))
            {
                return;
            }

            foreach (string entry in Directory.EnumerateFileSystemEntries(longPath))
            {
                ClearAttributes(entry);
            }

            File.SetAttributes(longPath, FileAttributes.Normal);
        }
        catch
        {
            // 忽略：尽力而为
        }
    }

    /// <summary>收尾：清理整个工作目录，日志保留到打印完</summary>
    public static void Cleanup()
    {
        try
        {
            _log?.Flush();
            _log?.Dispose();
            _log = null;
        }
        catch
        {
            // 忽略
        }

        try
        {
            LogManager.Flush();
        }
        catch
        {
            // 忽略
        }

        // 删除整个工作目录，先把日志复制到 %TEMP% 根供诊断
        try
        {
            string preserved = Path.Combine(Path.GetTempPath(), "cj-reg-last-log.txt");
            if (File.Exists(LogPath))
            {
                File.Copy(LogPath, preserved, overwrite: true);
            }
        }
        catch
        {
            // 忽略
        }

        DeleteTreeRobust(Root);
    }

    public static Stopwatch StartTimer() => Stopwatch.StartNew();
}
