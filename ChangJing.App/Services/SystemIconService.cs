using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChangJing.App.Core.FileSystem;
using ChangJing.App.Core.LongPath;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Services;

/// <summary>系统图标与类型名服务：按扩展名缓存，不逐文件读取</summary>
// 用 SHGFI_USEFILEATTRIBUTES 只按扩展名 / 属性推断，不打开文件、不触碰磁盘；图标 Freeze 后可跨线程缓存
public static class SystemIconService
{
    private const string DirectoryKey = "\u0001dir";
    private const string NoExtensionKey = "\u0001noext";
    private const string DriveKey = "\u0001drive";

    private static readonly ConcurrentDictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> TypeNameCache = new(StringComparer.OrdinalIgnoreCase);

    // 键 = 「真实路径 / 扩展名 + 尺寸档」
    private static readonly ConcurrentDictionary<string, ImageSource?> SizeIconCache = new(StringComparer.OrdinalIgnoreCase);

    // 键 = 「已知文件夹 Id + 尺寸档」
    private static readonly ConcurrentDictionary<string, ImageSource?> KnownFolderIconCache = new(StringComparer.OrdinalIgnoreCase);

    // 键 = 尺寸档
    private static readonly ConcurrentDictionary<string, ImageSource?> ShieldIconCache = new(StringComparer.OrdinalIgnoreCase);

    // 与 LocationProvider 的 Id 一致
    private static readonly Dictionary<string, Guid> KnownFolderIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Desktop"] = new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"),
        ["Documents"] = new Guid("FDD39AD0-238F-46AF-ADB4-6C85480369C7"),
        ["Downloads"] = new Guid("374DE290-123F-4565-9164-39C4925E467B"),
        ["Pictures"] = new Guid("33E28130-4E1E-4676-835A-98395C3BC3BB"),
        ["Music"] = new Guid("4BD8D571-6D19-48D3-BE97-422220080E43"),
        ["Videos"] = new Guid("18989B1D-99B5-455B-841C-AB7C74E4DDFC"),
    };

    /// <summary>取条目图标；失败时返回 null</summary>
    public static ImageSource? GetIcon(string name, bool isDirectory)
        => IconCache.GetOrAdd(CacheKey(name, isDirectory), static key => CreateIcon(key));

    /// <summary>取条目的类型名，文件夹固定「文件夹」</summary>
    public static string GetTypeName(string name, bool isDirectory)
    {
        if (isDirectory)
        {
            return "文件夹";
        }

        return TypeNameCache.GetOrAdd(CacheKey(name, isDirectory: false), static key => CreateTypeName(key));
    }

    /// <summary>驱动器小图标；失败时返回 null</summary>
    public static ImageSource? GetDriveIcon()
        => IconCache.GetOrAdd(DriveKey, static _ => CreateDriveIcon());

    /// <summary>按目标尺寸取驱动器图标；取不到时回退小图标</summary>
    public static ImageSource? GetDriveIcon(int size)
    {
        if (size <= 20)
        {
            return GetDriveIcon();
        }

        string key = $"\u0001drive\u0001{SizeBucket(size)}";
        ImageSource? icon = SizeIconCache.GetOrAdd(key, _ => CreateDriveShellIcon(size));
        return icon ?? GetDriveIcon();
    }

    /// <summary>按尺寸取驱动器图标；未就绪用文件属性推断不触碰磁盘</summary>
    public static ImageSource? GetDriveIconForPath(string rootPath, bool isReady, int size)
    {
        if (string.IsNullOrEmpty(rootPath))
        {
            return GetDriveIcon(size);
        }

        string key = $"\u0001drivepath\u0001{rootPath}\u0001{(isReady ? "r" : "n")}\u0001{SizeBucket(size)}";
        ImageSource? icon = SizeIconCache.GetOrAdd(key, _ => CreateDriveShellIconForPath(rootPath, isReady, size));
        return icon ?? GetDriveIcon(size);
    }

    /// <summary>按尺寸取条目图标：优先图像列表，取不到回退小图标</summary>
    public static ImageSource? GetIconForSize(string name, bool isDirectory, int size)
    {
        if (size <= 20)
        {
            return GetIcon(name, isDirectory);
        }

        string key = $"{CacheKey(name, isDirectory)}\u0001{SizeBucket(size)}";
        ImageSource? icon = SizeIconCache.GetOrAdd(key, _ => CreateSizeShellIcon(name, isDirectory, size));
        return icon ?? GetIcon(name, isDirectory);
    }

    /// <summary>取已知文件夹或回收站图标，取不到回退普通文件夹</summary>
    public static ImageSource? GetKnownFolderIcon(string knownFolderId, int size = 16)
    {
        if (string.IsNullOrEmpty(knownFolderId))
        {
            return GetIconForSize("folder", isDirectory: true, size);
        }

        string key = $"{knownFolderId}\u0001{SizeBucket(size)}";
        return KnownFolderIconCache.GetOrAdd(
            key,
            _ => CreateKnownFolderIcon(knownFolderId, size) ?? GetIconForSize("folder", isDirectory: true, size));
    }

    public static void ClearCache()
    {
        IconCache.Clear();
        TypeNameCache.Clear();
        SizeIconCache.Clear();
        KnownFolderIconCache.Clear();
        ShieldIconCache.Clear();
    }

    // 把目标边长归到尺寸档（16 / 32 / 48 / 256），避免每种像素各存一份
    private static string SizeBucket(int size)
        => size <= 20 ? "16" : size <= 40 ? "32" : size <= 64 ? "48" : "256";

    // 缓存键

    private static string CacheKey(string name, bool isDirectory)
    {
        if (isDirectory)
        {
            return DirectoryKey;
        }

        string extension = PathHelper.GetExtension(name);
        return extension.Length == 0 ? NoExtensionKey : extension;
    }

    // 图标 / 类型名创建

    private static ImageSource? CreateIcon(string key)
    {
        if (key == DirectoryKey)
        {
            return CreateShellIcon("folder", NativeMethods.FileAttributeDirectory);
        }

        if (key == NoExtensionKey)
        {
            return CreateShellIcon("changjing", NativeMethods.FileAttributeNormal);
        }

        return CreateShellIcon("changjing" + key, NativeMethods.FileAttributeNormal);
    }

    private static string CreateTypeName(string key)
    {
        string fakePath = key == NoExtensionKey ? "changjing" : "changjing" + key;

        SHFILEINFO info = Query(fakePath, NativeMethods.FileAttributeNormal, NativeMethods.ShgfiTypeName);
        string typeName = (info.szTypeName ?? string.Empty).Trim();

        if (typeName.Length == 0)
        {
            // 系统没有登记这个扩展名：给一个兜底文案
            string extension = key == NoExtensionKey ? string.Empty : key.TrimStart('.');
            typeName = extension.Length == 0 ? "文件" : extension.ToUpperInvariant() + " 文件";
        }

        return typeName;
    }

    private static ImageSource? CreateShellIcon(string fakePath, uint attributes)
        => ConvertIcon(Query(fakePath, attributes, NativeMethods.ShgfiIcon | NativeMethods.ShgfiSmallIcon | NativeMethods.ShgfiUseFileAttributes).hIcon);

    private static ImageSource? CreateDriveIcon()
    {
        // 用系统盘根目录取「本地磁盘」图标（固定盘，必然就绪，不会触发「请插入磁盘」）
        return ConvertIcon(Query(GetSystemDriveRoot(), NativeMethods.FileAttributeDirectory, NativeMethods.ShgfiIcon | NativeMethods.ShgfiSmallIcon).hIcon);
    }

    private static string GetSystemDriveRoot()
    {
        string systemRoot = Environment.SystemDirectory;
        return systemRoot.Length >= 3 ? systemRoot.Substring(0, 3) : "C:\\";
    }

    // 按尺寸取图标（SHGetImageList）

    // 先取图标索引，再从系统图像列表取对应尺寸
    private static ImageSource? CreateDriveShellIcon(int size)
        => CreateShellIconAtSize(GetSystemDriveRoot(), NativeMethods.FileAttributeDirectory, size, useFileAttributes: false);

    // 未就绪时用文件属性推断，避免触碰磁盘弹出「请插入磁盘」
    private static ImageSource? CreateDriveShellIconForPath(string rootPath, bool isReady, int size)
        => CreateShellIconAtSize(
            rootPath,
            NativeMethods.FileAttributeDirectory,
            size,
            useFileAttributes: !isReady);

    // SHGFI_USEFILEATTRIBUTES，不触碰磁盘
    private static ImageSource? CreateSizeShellIcon(string name, bool isDirectory, int size)
    {
        uint attributes = isDirectory ? NativeMethods.FileAttributeDirectory : NativeMethods.FileAttributeNormal;
        return CreateShellIconAtSize(name, attributes, size, useFileAttributes: true);
    }

    // 取索引后用 SHGetImageList 取目标尺寸的图标
    private static ImageSource? CreateShellIconAtSize(string path, uint attributes, int size, bool useFileAttributes)
    {
        try
        {
            uint flags = NativeMethods.ShgfiSysIconIndex;
            if (useFileAttributes)
            {
                flags |= NativeMethods.ShgfiUseFileAttributes;
            }

            if (!TryQuery(path, attributes, flags, out SHFILEINFO info))
            {
                return null;
            }

            return GetIconFromImageList(info.iIcon, size);
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(SystemIconService), $"取 {size}px 图标失败（{path}）：{ex.Message}");
            return null;
        }
    }

    // 取不到时逐级回退到更小的列表
    private static ImageSource? GetIconFromImageList(int iconIndex, int size)
    {
        if (iconIndex < 0)
        {
            return null;
        }

        Guid iid = typeof(ShellImageNativeMethods.IImageList).GUID;
        foreach (int shil in ImageListFallbackOrder(size))
        {
            if (ShellImageNativeMethods.SHGetImageList(shil, ref iid, out ShellImageNativeMethods.IImageList list) != 0
                || list is null)
            {
                continue;
            }

            try
            {
                if (list.GetIcon(iconIndex, ShellImageNativeMethods.IldTransparent, out IntPtr hIcon) == 0
                    && hIcon != IntPtr.Zero)
                {
                    return ConvertIcon(hIcon);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(list);
            }
        }

        return null;
    }

    /// <summary>取 UAC 管理员小盾牌图标；按尺寸档缓存，取不到时返回 null</summary>
    public static ImageSource? GetShieldIcon(int size = 16)
        => ShieldIconCache.GetOrAdd(SizeBucket(size), _ => CreateShieldIcon(size));

    // 目标尺寸对应的系统图像列表（SHIL_*），随后按由大到小回退
    private static int[] ImageListFallbackOrder(int size)
    {
        int primary = size <= 20
            ? ShellImageNativeMethods.ShilSmall
            : size <= 40
                ? ShellImageNativeMethods.ShilLarge
                : size <= 72
                    ? ShellImageNativeMethods.ShilExtraLarge
                    : ShellImageNativeMethods.ShilJumbo;

        return primary switch
        {
            ShellImageNativeMethods.ShilJumbo => new[]
            {
                ShellImageNativeMethods.ShilJumbo,
                ShellImageNativeMethods.ShilExtraLarge,
                ShellImageNativeMethods.ShilLarge,
                ShellImageNativeMethods.ShilSmall,
            },
            ShellImageNativeMethods.ShilExtraLarge => new[]
            {
                ShellImageNativeMethods.ShilExtraLarge,
                ShellImageNativeMethods.ShilLarge,
                ShellImageNativeMethods.ShilSmall,
            },
            ShellImageNativeMethods.ShilLarge => new[]
            {
                ShellImageNativeMethods.ShilLarge,
                ShellImageNativeMethods.ShilSmall,
            },
            _ => new[]
            {
                ShellImageNativeMethods.ShilSmall,
                ShellImageNativeMethods.ShilLarge,
            },
        };
    }

    // 已知文件夹 / 回收站图标

    private static ImageSource? CreateKnownFolderIcon(string knownFolderId, int size)
    {
        if (string.Equals(knownFolderId, "RecycleBin", StringComparison.OrdinalIgnoreCase))
        {
            return CreateStockIcon(ShellImageNativeMethods.SiidRecycleBin, size);
        }

        if (!KnownFolderIds.TryGetValue(knownFolderId, out Guid folderId))
        {
            return null;
        }

        IntPtr pidl = IntPtr.Zero;
        try
        {
            int hr = ShellImageNativeMethods.SHGetKnownFolderIDList(ref folderId, 0, IntPtr.Zero, out pidl);
            if (hr != 0 || pidl == IntPtr.Zero)
            {
                return null;
            }

            IntPtr result = ShellImageNativeMethods.SHGetFileInfoW(
                pidl,
                0,
                out ShellImageNativeMethods.SHFILEINFO info,
                (uint)Marshal.SizeOf<ShellImageNativeMethods.SHFILEINFO>(),
                ShellImageNativeMethods.ShgfiPidl | ShellImageNativeMethods.ShgsiSysIconIndex);

            if (result == IntPtr.Zero)
            {
                return null;
            }

            return GetIconFromImageList(info.iIcon, size);
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(SystemIconService), $"取已知文件夹图标失败（{knownFolderId}）：{ex.Message}");
            return null;
        }
        finally
        {
            if (pidl != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        }
    }

    private static ImageSource? CreateStockIcon(uint stockIconId, int size)
    {
        try
        {
            var info = new ShellImageNativeMethods.SHSTOCKICONINFO
            {
                cbSize = (uint)Marshal.SizeOf<ShellImageNativeMethods.SHSTOCKICONINFO>(),
            };

            int hr = ShellImageNativeMethods.SHGetStockIconInfo(
                stockIconId,
                ShellImageNativeMethods.ShgsiSysIconIndex,
                ref info);

            return hr != 0 ? null : GetIconFromImageList(info.iSysImageIndex, size);
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(SystemIconService), $"取系统图标（StockIcon {stockIconId}）失败：{ex.Message}");
            return null;
        }
    }

    private static ImageSource? CreateShieldIcon(int size)
        => CreateStockIcon(ShellImageNativeMethods.SiidShield, size);

    private static SHFILEINFO Query(string path, uint attributes, uint flags)
    {
        return TryQuery(path, attributes, flags, out SHFILEINFO info) ? info : default;
    }

    private static bool TryQuery(string path, uint attributes, uint flags, out SHFILEINFO info)
    {
        info = default;

        try
        {
            IntPtr result = NativeMethods.SHGetFileInfoW(
                path,
                attributes,
                out info,
                (uint)Marshal.SizeOf<SHFILEINFO>(),
                flags);

            return result != IntPtr.Zero;
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(SystemIconService), $"取系统图标 / 类型名失败（{path}）：{ex.Message}");
            return false;
        }
    }

    private static ImageSource? ConvertIcon(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            ImageSource source = Imaging.CreateBitmapSourceFromHIcon(
                hIcon,
                System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            source.Freeze();
            return source;
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(SystemIconService), $"HICON 转 ImageSource 失败：{ex.Message}");
            return null;
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }

    // 与 Win32 SHFILEINFO 布局一致
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private static class NativeMethods
    {
        internal const uint FileAttributeNormal = 0x0000_0080;
        internal const uint FileAttributeDirectory = 0x0000_0010;

        internal const uint ShgfiIcon = 0x0000_0100;
        internal const uint ShgfiTypeName = 0x0000_0400;
        internal const uint ShgfiSmallIcon = 0x0000_0001;
        internal const uint ShgfiUseFileAttributes = 0x0000_0010;
        internal const uint ShgfiSysIconIndex = 0x0000_4000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
        internal static extern IntPtr SHGetFileInfoW(
            string pszPath,
            uint dwFileAttributes,
            out SHFILEINFO psfi,
            uint cbFileInfo,
            uint uFlags);

        [DllImport("user32.dll", SetLastError = false)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(IntPtr hIcon);
    }
}
