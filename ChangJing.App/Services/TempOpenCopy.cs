using System.IO;
using ChangJing.App.Core.FileSystem;
using ChangJing.App.Core.LongPath;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Services;

/// <summary>临时副本：联接不可用或名称特殊时复制到 tmp 用合法名打开</summary>
// 临时副本只用于查看，修改不回写；退出清理，被改过提示另存回
public static class TempOpenCopy
{
    /// <summary>一次临时副本的记录</summary>
    public sealed class CopyRecord
    {
        internal CopyRecord(string sourceDisplayPath, string copiedDisplayPath, string directory, DateTime copiedWriteTimeUtc)
        {
            SourceDisplayPath = sourceDisplayPath;
            CopiedDisplayPath = copiedDisplayPath;
            Directory = directory;
            CopiedWriteTimeUtc = copiedWriteTimeUtc;
        }

        /// <summary>源文件普通路径</summary>
        public string SourceDisplayPath { get; }

        /// <summary>临时副本普通路径</summary>
        public string CopiedDisplayPath { get; }

        public string Directory { get; }

        /// <summary>创建副本时的修改时间，用于判断副本是否被外部程序修改</summary>
        public DateTime CopiedWriteTimeUtc { get; }
    }

    private static readonly object Gate = new();
    private static readonly List<CopyRecord> Records = new();
    private static int _nextIndex = 1;

    /// <summary>临时副本根目录：%LOCALAPPDATA%\ChangJing\tmp</summary>
    public static string RootDirectory
        => string.IsNullOrEmpty(RootOverride) ? AppPaths.TempCopyRootDirectory : RootOverride!;

    /// <summary>临时副本根目录覆盖，避免验证污染真实的 %LOCALAPPDATA%\ChangJing\tmp</summary>
    public static string? RootOverride { get; set; }

    public static IReadOnlyList<CopyRecord> ActiveCopies
    {
        get
        {
            lock (Gate)
            {
                return Records.ToArray();
            }
        }
    }

    public static bool TryCopy(string sourceDisplayPath, out string? copiedDisplayPath, out string error)
        => TryCopy(sourceDisplayPath, out copiedDisplayPath, out error, out _);

    /// <summary>复制到临时目录下的合法名，返回新路径与副本记录</summary>
    public static bool TryCopy(
        string sourceDisplayPath,
        out string? copiedDisplayPath,
        out string error,
        out CopyRecord? record)
    {
        copiedDisplayPath = null;
        record = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(sourceDisplayPath))
        {
            error = "路径为空。";
            return false;
        }

        string display = PathHelper.ToDisplay(sourceDisplayPath);
        string legalName = BuildLegalName(PathHelper.GetName(display));

        string directory;
        string destination;
        lock (Gate)
        {
            int index = _nextIndex++;
            directory = Path.Combine(RootDirectory, index.ToString());
            destination = Path.Combine(directory, legalName);
        }

        // 副本路径必须短于 260（ShellExecuteEx 只接受普通路径）：名字过长时截断（保留扩展名）
        destination = ShortenForOpen(destination, directory, legalName);

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(TempOpenCopy), $"创建临时目录失败：{ex.Message}");
            error = "无法创建临时文件夹，详细信息已写入日志。";
            return false;
        }

        string sourceLong;
        try
        {
            sourceLong = PathHelper.ToLong(display);
        }
        catch (Exception ex)
        {
            error = $"源路径不可用：{ex.Message}";
            return false;
        }

        int cancel = 0;
        bool ok;
        try
        {
            ok = NativeMethods.CopyFileExW(
                sourceLong,
                destination,
                null,
                IntPtr.Zero,
                ref cancel,
                NativeMethods.CopyFileFailIfExists);
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(TempOpenCopy), ex);
            error = "复制临时副本时发生错误，详细信息已写入日志。";
            return false;
        }

        if (!ok)
        {
            int code = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            string reason = Win32ErrorMessages.Describe(code);
            LogManager.Warning(typeof(TempOpenCopy), $"复制临时副本失败：{display} — {reason}（错误码 {code}）");
            error = $"无法创建临时副本：{reason}";
            return false;
        }

        // 记录副本的修改时间，用于判断「是否被外部程序改过」
        DateTime copiedWriteUtc = DateTime.MinValue;
        if (File.Exists(destination))
        {
            try
            {
                copiedWriteUtc = File.GetLastWriteTimeUtc(destination);
            }
            catch
            {
                // 忽略：取不到时间就按「未修改」处理
            }
        }

        var newRecord = new CopyRecord(display, destination, directory, copiedWriteUtc);
        lock (Gate)
        {
            Records.Add(newRecord);
        }

        LogManager.Info(typeof(TempOpenCopy), $"已创建临时副本：{display} → {destination}");
        copiedDisplayPath = destination;
        record = newRecord;
        return true;
    }

    public static bool IsModified(CopyRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        try
        {
            if (!File.Exists(record.CopiedDisplayPath))
            {
                return false;
            }

            return File.GetLastWriteTimeUtc(record.CopiedDisplayPath) != record.CopiedWriteTimeUtc;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>清理所有临时副本目录，被占用的留到下次启动</summary>
    public static IReadOnlyList<CopyRecord> CleanupAll()
    {
        lock (Gate)
        {
            var locked = new List<CopyRecord>();

            foreach (CopyRecord record in Records)
            {
                if (!TryDeleteDirectory(record.Directory))
                {
                    locked.Add(record);
                }
            }

            Records.Clear();
            Records.AddRange(locked);
            return locked.ToArray();
        }
    }

    /// <summary>启动清扫：删除 tmp\ 下超过 1 天或未占用的残留目录</summary>
    public static void SweepResidue()
    {
        try
        {
            if (!Directory.Exists(RootDirectory))
            {
                return;
            }

            DateTime cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (string dir in Directory.EnumerateDirectories(RootDirectory))
            {
                try
                {
                    bool stale = Directory.GetLastWriteTimeUtc(dir) < cutoff;
                    if (stale || !IsLocked(dir))
                    {
                        TryDeleteDirectory(dir);
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Debug(typeof(TempOpenCopy), $"清扫残留副本失败（{dir}）：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(TempOpenCopy), $"清扫临时副本目录失败：{ex.Message}");
        }
    }

    // 用现有复制引擎的同一套原语（先写临时名 name.cjtmp，校验后 MoveFileExW 覆盖），保证中途失败时原文件仍在
    public static bool TrySaveBack(CopyRecord record, out string error)
    {
        ArgumentNullException.ThrowIfNull(record);
        error = string.Empty;

        string sourceLong;
        string targetLong;
        try
        {
            sourceLong = PathHelper.ToLong(record.CopiedDisplayPath);
            targetLong = PathHelper.ToLong(record.SourceDisplayPath);
        }
        catch (Exception ex)
        {
            error = $"路径不可用：{ex.Message}";
            return false;
        }

        string tempLong = targetLong + ".cjtmp";
        int cancel = 0;

        // 先复制到目标同目录的临时名（不覆盖原文件）
        bool copied;
        try
        {
            copied = NativeMethods.CopyFileExW(sourceLong, tempLong, null, IntPtr.Zero, ref cancel, 0);
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(TempOpenCopy), ex);
            error = "另存回原位置时发生错误，详细信息已写入日志。";
            return false;
        }

        if (!copied)
        {
            int code = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            error = $"另存回原位置失败：{Win32ErrorMessages.Describe(code)}";
            TryDeleteFile(tempLong);
            return false;
        }

        // 临时名成功后再覆盖原文件（失败时原文件仍在）
        if (!NativeMethods.MoveFileExW(tempLong, targetLong, NativeMethods.MoveFileReplaceExisting))
        {
            int code = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            error = $"覆盖原文件失败：{Win32ErrorMessages.Describe(code)}";
            TryDeleteFile(tempLong);
            return false;
        }

        LogManager.Info(typeof(TempOpenCopy), $"已将临时副本另存回原位置：{record.SourceDisplayPath}");
        return true;
    }

    public static string BuildLegalName(string name)
    {
        string cleaned = (name ?? string.Empty).TrimEnd('.', ' ');

        if (cleaned.Length == 0)
        {
            cleaned = "file";
        }

        if (LongPathRules.IsReservedName(cleaned))
        {
            // 保留名（CON、NUL、COM1…）：加前缀使其合法，保留扩展名
            cleaned = "_" + cleaned;
        }

        return cleaned;
    }

    // 内部

    // 超过 259 时截断文件名（保留扩展名）使整条路径短于 260
    private static string ShortenForOpen(string path, string directory, string legalName)
    {
        if (path.Length <= LongPathRules.MaxExplorerPathLength)
        {
            return path;
        }

        int room = LongPathRules.MaxExplorerPathLength - directory.Length - 1;
        if (room < 8)
        {
            // 目录本身就太长：无法给出短于 260 的副本路径（调用方已尽量选短目录）
            return path;
        }

        string extension = PathHelper.GetExtension(legalName);
        int keep = Math.Max(1, room - extension.Length);
        string stem = legalName.Length > extension.Length
            ? legalName.Substring(0, legalName.Length - extension.Length)
            : legalName;

        if (stem.Length > keep)
        {
            stem = stem.Substring(0, keep);
        }

        return Path.Combine(directory, stem + extension);
    }

    private static bool IsLocked(string directory)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException)
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            // 连枚举都失败：当作被占用处理（不在清扫时强删）
            return true;
        }
    }

    // 被占用返回 false（保留待下次启动清理）
    private static bool TryDeleteDirectory(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return true;
            }

            // 临时副本可能是只读 / 隐藏：先去掉只读再删
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch
                {
                    // 忽略：删除失败会在下面捕获
                }
            }

            Directory.Delete(directory, recursive: true);
            LogManager.Info(typeof(TempOpenCopy), $"已清理临时副本目录：{directory}");
            return true;
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(TempOpenCopy), $"清理临时副本目录失败（可能被占用）：{directory} — {ex.Message}");
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch
        {
            // 忽略
        }
    }
}
