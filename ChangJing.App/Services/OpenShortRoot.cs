using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ChangJing.App.Core.FileSystem;
using ChangJing.App.Core.LongPath;
using CustomWin.Utils.Logs;
using Microsoft.Win32.SafeHandles;

namespace ChangJing.App.Services;

/// <summary>把长路径文件所在文件夹用联接挂到短根，得到短普通路径</summary>
// 不用 DefineDosDevice 映射盘符，否则会出现在「此电脑」里很怪
public static class OpenShortRoot
{
    private static readonly object Gate = new();

    private static readonly Dictionary<string, (string Name, string JunctionPath, string TargetFolder)> Links =
        new(StringComparer.OrdinalIgnoreCase);

    private static int _nextName = 1;

    /// <summary>短根目录覆盖，仅验证用，避免污染真实的 %LOCALAPPDATA%\ChangJing\o</summary>
    public static string? RootOverride { get; set; }

    /// <summary>短根目录候选：按顺序取联接后仍短于 260 的第一个</summary>
    // 默认 %LOCALAPPDATA%\ChangJing\o，仍超限试 %TEMP%\cjo，都不行回退临时副本
    public static IReadOnlyList<string> CandidateRoots
    {
        get
        {
            if (!string.IsNullOrEmpty(RootOverride))
            {
                return new[] { RootOverride! };
            }

            return new[]
            {
                AppPaths.OpenShortRootDirectory,
                Path.Combine(Path.GetTempPath(), "cjo"),
            };
        }
    }

    /// <summary>当前已建立的联接数量，供验证读取</summary>
    public static int LinkCount
    {
        get
        {
            lock (Gate)
            {
                return Links.Count;
            }
        }
    }

    /// <summary>尝试为 fileDisplayPath 准备短路径：短根\短名\文件名</summary>
    public static bool TryPrepareFile(string fileDisplayPath, out string? shortPath)
    {
        shortPath = null;

        if (string.IsNullOrWhiteSpace(fileDisplayPath))
        {
            return false;
        }

        string display = PathHelper.ToDisplay(fileDisplayPath);
        string name = PathHelper.GetName(display);

        // 名称特殊时 Win32 打不开，联接也没用，改走临时副本
        if (LongPathRules.HasTrailingDotOrSpace(name) || LongPathRules.IsReservedName(name))
        {
            return false;
        }

        string? folder = PathHelper.GetParent(display);
        if (string.IsNullOrEmpty(folder))
        {
            return false;
        }

        string folderNormalized = NormalizeFolder(folder!);
        if (folderNormalized.Length == 0)
        {
            return false;
        }

        lock (Gate)
        {
            // 同一文件夹复用同一联接
            if (!Links.TryGetValue(folderNormalized, out (string Name, string JunctionPath, string TargetFolder) existing))
            {
                if (!TryCreateJunction(folderNormalized, name, out existing))
                {
                    return false;
                }

                Links[folderNormalized] = existing;
            }

            string candidate = Path.Combine(existing.JunctionPath, name);
            if (candidate.Length > LongPathRules.MaxExplorerPathLength)
            {
                // 联接后仍超限，调用方回退临时副本
                LogManager.Info(
                    typeof(OpenShortRoot),
                    $"联接后路径仍超过 {LongPathRules.MaxExplorerPathLength}，改用临时副本：{candidate}");
                return false;
            }

            shortPath = candidate;
            return true;
        }
    }

    /// <summary>移除所有已建立的联接，退出时调用，不抛异常</summary>
    public static void RemoveAll()
    {
        lock (Gate)
        {
            foreach ((string _, (string _, string junctionPath, string _)) in Links)
            {
                TryRemoveJunction(junctionPath);
            }

            Links.Clear();
            _nextName = 1;
        }
    }

    /// <summary>启动清扫 o\ 下残留子目录，只删联接，非重解析点不动</summary>
    public static void SweepResidue()
    {
        foreach (string root in CandidateRoots)
        {
            SweepRoot(root);
        }
    }

    private static void SweepRoot(string root)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                uint attributes = NativeMethods.GetFileAttributesW(PathHelper.ToLong(dir));
                if (attributes == NativeMethods.InvalidFileAttributes
                    || (attributes & NativeMethods.FileAttributeReparsePoint) == 0)
                {
                    // 不是重解析点就绝不动，避免误删
                    continue;
                }

                TryRemoveJunction(dir);
            }
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(OpenShortRoot), $"清扫短根残留失败（{root}）：{ex.Message}");
        }
    }

    // 内部：建立 / 删除联接

    private static bool TryCreateJunction(
        string targetFolder,
        string fileName,
        out (string Name, string JunctionPath, string TargetFolder) link)
    {
        link = default;

        // 网络共享 UNC 无法做本地联接目标，调用方回退临时副本
        if (targetFolder.StartsWith(@"\\", StringComparison.Ordinal))
        {
            LogManager.Info(typeof(OpenShortRoot), $"目标在网络共享，跳过联接：{targetFolder}");
            return false;
        }

        // 依次试候选短根，取联接后仍短于 260 的第一个
        foreach (string root in EnumerateRootsFor(targetFolder))
        {
            if (!TryEnsureRoot(root))
            {
                continue;
            }

            for (int attempt = 0; attempt < 64; attempt++)
            {
                string name = (_nextName++).ToString();
                string junctionPath = Path.Combine(root, name);

                // 名称可能被上次残留占用，跳过已存在的
                if (Directory.Exists(junctionPath) || File.Exists(junctionPath))
                {
                    continue;
                }

                string candidate = Path.Combine(junctionPath, fileName);
                if (candidate.Length > LongPathRules.MaxExplorerPathLength)
                {
                    // 该短根太长，联接后仍超限，换下一个根
                    break;
                }

                if (!TryCreateJunctionCore(junctionPath, targetFolder))
                {
                    break;
                }

                link = (name, junctionPath, targetFolder);
                LogManager.Info(typeof(OpenShortRoot), $"已建立目录联接：{junctionPath} → {targetFolder}");
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateRootsFor(string targetFolder)
    {
        foreach (string root in CandidateRoots)
        {
            yield return root;
        }

        // 验证模式只用覆盖目录，绝不落到真实卷根
        if (!string.IsNullOrEmpty(RootOverride))
        {
            yield break;
        }

        // 同卷根如 C:\$cjo；根目录需管理员，创建失败会跳过
        string? volumeRoot = GetVolumeRoot(targetFolder);
        if (!string.IsNullOrEmpty(volumeRoot))
        {
            yield return Path.Combine(volumeRoot!, "$cjo");
        }
    }

    private static string? GetVolumeRoot(string folder)
    {
        if (folder.Length >= 2 && folder[1] == ':')
        {
            return folder.Substring(0, 2) + "\\";
        }

        return null;
    }

    private static bool TryEnsureRoot(string root)
    {
        try
        {
            Directory.CreateDirectory(root);
            return true;
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(OpenShortRoot), $"无法创建短根目录（{root}）：{ex.Message}");
            return false;
        }
    }

    private static bool TryCreateJunctionCore(string junctionPath, string targetFolder)
    {
        try
        {
            Directory.CreateDirectory(junctionPath);
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(OpenShortRoot), $"无法创建联接占位目录（{junctionPath}）：{ex.Message}");
            return false;
        }

        if (TrySetMountPointReparse(junctionPath, targetFolder))
        {
            return true;
        }

        // 备选目录符号链接需开发者模式，目标超长时跳过
        if (targetFolder.Length <= LongPathRules.MaxExplorerPathLength &&
            NativeMethods.CreateSymbolicLinkW(junctionPath, targetFolder, NativeMethods.SymbolicLinkFlagDirectory))
        {
            return true;
        }

        // 两种都失败就清理占位目录，避免残留
        try
        {
            NativeMethods.RemoveDirectoryW(PathHelper.ToLong(junctionPath));
        }
        catch
        {
            // 忽略
        }

        return false;
    }

    // 用 FSCTL_SET_REPARSE_POINT 把目录设为指向目标文件夹的装入点
    private static bool TrySetMountPointReparse(string junctionPath, string targetFolder)
    {
        // MOUNT_POINT_REPARSE_BUFFER：4 个 ushort 偏移长度 + PathBuffer，外层头 8 字节
        string substitute = @"\??\" + targetFolder;
        byte[] substituteBytes = Encoding.Unicode.GetBytes(substitute);
        byte[] printBytes = Encoding.Unicode.GetBytes(targetFolder);

        int pathBufferLength = substituteBytes.Length + 2 + printBytes.Length + 2;   // 含两个 NUL
        int reparseDataLength = 8 + pathBufferLength;                               // 4 个 ushort 头
        int total = 8 + reparseDataLength;                                          // 外层 8 字节头
        var buffer = new byte[total];

        BitConverter.GetBytes(NativeMethods.IoReparseTagMountPoint).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)reparseDataLength).CopyTo(buffer, 4);
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 6);                              // Reserved
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 8);                              // SubstituteNameOffset
        BitConverter.GetBytes((ushort)substituteBytes.Length).CopyTo(buffer, 10);        // SubstituteNameLength
        BitConverter.GetBytes((ushort)(substituteBytes.Length + 2)).CopyTo(buffer, 12);  // PrintNameOffset 跨过 NUL
        BitConverter.GetBytes((ushort)printBytes.Length).CopyTo(buffer, 14);             // PrintNameLength
        substituteBytes.CopyTo(buffer, 16);
        // buffer 里 SubstituteName 的 NUL 保持为 0
        printBytes.CopyTo(buffer, 16 + substituteBytes.Length + 2);
        // 末尾 2 字节保持为 0，即 PrintName 的 NUL

        using SafeFileHandle handle = NativeMethods.CreateFileW(
            PathHelper.ToLong(junctionPath),
            NativeMethods.GenericWrite,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagOpenReparsePoint | NativeMethods.FileFlagBackupSemantics,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int openError = Marshal.GetLastWin32Error();
            LogManager.Debug(typeof(OpenShortRoot), $"打开联接目录失败（{junctionPath}，错误码 {openError}）。");
            return false;
        }

        if (!NativeMethods.DeviceIoControl(
                handle,
                NativeMethods.FsctlSetReparsePoint,
                buffer,
                (uint)buffer.Length,
                IntPtr.Zero,
                0,
                out _,
                IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            LogManager.Debug(typeof(OpenShortRoot), $"设置目录联接失败（{junctionPath} → {targetFolder}，错误码 {error}）。");
            return false;
        }

        return true;
    }

    // 只删联接本身：删前确认属性含 REPARSE_POINT，否则不动，绝不穿透删目标内容
    private static void TryRemoveJunction(string junctionPath)
    {
        try
        {
            string longPath = PathHelper.ToLong(junctionPath);
            uint attributes = NativeMethods.GetFileAttributesW(longPath);

            if (attributes == NativeMethods.InvalidFileAttributes)
            {
                return;   // 已不存在
            }

            if ((attributes & NativeMethods.FileAttributeReparsePoint) == 0)
            {
                // 不是重解析点：可能是普通目录，保守起见不动它，只记日志
                LogManager.Warning(typeof(OpenShortRoot), $"跳过删除（不是重解析点）：{junctionPath}");
                return;
            }

            if (NativeMethods.RemoveDirectoryW(longPath))
            {
                LogManager.Info(typeof(OpenShortRoot), $"已删除目录联接：{junctionPath}");
            }
            else
            {
                int error = Marshal.GetLastWin32Error();
                LogManager.Warning(typeof(OpenShortRoot), $"删除目录联接失败（{junctionPath}，错误码 {error}）。");
            }
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(OpenShortRoot), $"删除目录联接异常（{junctionPath}）：{ex.Message}");
        }
    }

    private static string NormalizeFolder(string folder)
    {
        try
        {
            return PathHelper.ToDisplay(PathHelper.Normalize(folder)).TrimEnd('\\');
        }
        catch
        {
            return string.Empty;
        }
    }
}
