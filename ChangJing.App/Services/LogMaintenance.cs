using System.IO;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Services;

/// <summary>日志文件维护：清理历史日志，避免日志目录无限增长</summary>
// EnableFileLimit 只切文件不删旧文件，故启动时清理旧日志
public static class LogMaintenance
{
    /// <summary>默认保留的日志文件个数，含当前文件</summary>
    public const int DefaultKeepFiles = 6;

    /// <summary>清理 directory 下形如 {baseName}*.log 的历史日志，只保留最新的若干个</summary>
    public static void Prune(string directory, string baseName, int keepFiles = DefaultKeepFiles)
    {
        try
        {
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(baseName) || keepFiles < 1)
            {
                return;
            }

            if (!Directory.Exists(directory))
            {
                return;
            }

            string pattern = baseName + "*.log";
            var files = new List<FileInfo>();
            foreach (string path in Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly))
            {
                files.Add(new FileInfo(path));
            }

            if (files.Count <= keepFiles)
            {
                return;
            }

            files.Sort(static (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));

            int deleted = 0;
            for (int i = keepFiles; i < files.Count; i++)
            {
                try
                {
                    File.Delete(files[i].FullName);
                    deleted++;
                }
                catch
                {
                    // 单个文件删除失败，被占用等不影响其它文件
                }
            }

            if (deleted > 0)
            {
                LogManager.Info(typeof(LogMaintenance), $"已清理 {deleted} 个历史日志文件（保留最新 {keepFiles} 个）。");
            }
        }
        catch (Exception ex)
        {
            try
            {
                LogManager.Warning(typeof(LogMaintenance), $"清理历史日志失败：{ex.Message}");
            }
            catch
            {
                // 忽略
            }
        }
    }
}
