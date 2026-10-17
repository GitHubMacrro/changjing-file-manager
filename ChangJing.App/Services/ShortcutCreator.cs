using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ChangJing.App.Core.LongPath;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Services;

/// <summary>在指定文件夹内创建指向目标的 .lnk 快捷方式</summary>
// 用 Shell 的 IShellLinkW + IPersistFile 创建；Save 只接受普通（显示）路径，不接受 \\?\ 扩展路径
public static class ShortcutCreator
{
    // 新建快捷方式默认基名
    private const string DefaultBaseName = "新建 快捷方式";

    // 唯一名称的最大尝试次数（防止极端情况下死循环）
    private const int MaxAttempts = 500;

    // 资源管理器 / Shell 可处理的最大普通路径长度（与 MAX_PATH 一致）
    private const int MaxConsumablePathLength = 259;

    private const string ShortcutExtension = ".lnk";

    // Windows 保留设备名（不区分大小写，无扩展名时判定）
    private static readonly string[] ReservedDeviceNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>在 folderDisplayPath 下创建指向 targetDisplayPath 的快捷方式</summary>
    public static bool TryCreate(
        string folderDisplayPath,
        string targetDisplayPath,
        out string? createdDisplayPath,
        out string error)
    {
        createdDisplayPath = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(folderDisplayPath))
        {
            error = "未指定快捷方式所在文件夹。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(targetDisplayPath))
        {
            error = "未指定快捷方式的目标。";
            return false;
        }

        // 统一成普通路径：Shell 的保存与目标写入都只接受普通路径
        string folder = PathHelper.ToDisplay(folderDisplayPath);
        string target = PathHelper.ToDisplay(targetDisplayPath);

        string? linkNormalPath = BuildUniqueLinkPath(folder);
        if (linkNormalPath is null)
        {
            error = "无法为该文件夹生成可用的快捷方式名称。";
            return false;
        }

        IShellLinkW? link = null;
        try
        {
            link = (IShellLinkW)new ShellLinkCoClass();
            link.SetPath(target);

            string? parent = PathHelper.GetParent(target);
            if (!string.IsNullOrEmpty(parent))
            {
                link.SetWorkingDirectory(PathHelper.ToDisplay(parent!));
            }

            var persistFile = (IPersistFile)link;
            persistFile.Save(linkNormalPath, true);
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(ShortcutCreator), ex);
            error = $"创建快捷方式失败：{ex.Message}";
            return false;
        }
        finally
        {
            if (link is not null)
            {
                try
                {
                    Marshal.ReleaseComObject(link);
                }
                catch (Exception ex)
                {
                    LogManager.Warning(typeof(ShortcutCreator), $"释放 ShellLink COM 对象失败：{ex.Message}");
                }
            }
        }

        createdDisplayPath = linkNormalPath;
        return true;
    }

    /// <summary>目标路径是否对别的程序有风险：长度 > 259、UNC、名称以点 / 空格结尾、或最后一段是保留设备名</summary>
    public static bool IsTargetRisky(string targetDisplayPath)
    {
        if (string.IsNullOrEmpty(targetDisplayPath))
        {
            return false;
        }

        string path = PathHelper.ToDisplay(targetDisplayPath);

        if (path.Length > MaxConsumablePathLength)
        {
            return true;
        }

        // 网络路径：其它程序（Shell / COM 组件）对 UNC 支持不完整
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        string name = PathHelper.GetName(path);
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (name[^1] == '.' || name[^1] == ' ')
        {
            return true;
        }

        // 保留设备名：无扩展名的整名匹配（带扩展名不算风险，如 CON.txt 可被正常处理）
        if (name.IndexOf('.') < 0)
        {
            foreach (string reserved in ReservedDeviceNames)
            {
                if (string.Equals(name, reserved, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? BuildUniqueLinkPath(string folder)
    {
        for (int index = 0; index < MaxAttempts; index++)
        {
            string baseName = index == 0 ? DefaultBaseName : $"{DefaultBaseName} ({index + 1})";
            string candidate = PathHelper.Combine(folder, baseName + ShortcutExtension);

            if (!ExistsForFileSystem(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    // 用扩展路径做存在性判断，正确覆盖超长路径
    private static bool ExistsForFileSystem(string displayPath)
    {
        try
        {
            return File.Exists(PathHelper.ToLong(displayPath));
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(ShortcutCreator), $"判断快捷方式名称是否占用失败：{ex.Message}");
            return false;
        }
    }

    // 本地 COM 声明

    /// <summary>CLSID_ShellLink</summary>
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    internal class ShellLinkCoClass
    {
    }

    /// <summary>IID_IShellLinkW</summary>
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComConversionLoss]
    internal interface IShellLinkW
    {
        void GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
            int cch,
            IntPtr pfd,
            uint fFlags);

        void GetIDList(out IntPtr ppidl);

        void SetIDList(IntPtr pidl);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        void GetHotkey(out short pwHotkey);

        void SetHotkey(short wHotkey);

        void GetShowCmd(out int piShowCmd);

        void SetShowCmd(int iShowCmd);

        void GetIconLocation(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath,
            int cch,
            out int piIcon);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

        void Resolve(IntPtr hwnd, uint fFlags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    /// <summary>IID_IPersistFile</summary>
    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPersistFile
    {
        void GetClassID(out Guid pClassID);

        void IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);

        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);

        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
