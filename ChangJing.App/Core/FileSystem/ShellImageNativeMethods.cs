using System.Runtime.InteropServices;

namespace ChangJing.App.Core.FileSystem;

/// <summary>缩略图与大图标相关的 Win32 / Shell COM 互操作</summary>
internal static class ShellImageNativeMethods
{
    internal const uint ShgfiIcon = 0x0000_0100;
    internal const uint ShgfiSmallIcon = 0x0000_0001;
    internal const uint ShgfiLargeIcon = 0x0000_0000;
    internal const uint ShgfiUseFileAttributes = 0x0000_0010;
    internal const uint ShgfiSysIconIndex = 0x0000_4000;
    internal const uint ShgfiPidl = 0x0000_0008;

    internal const uint FileAttributeDirectory = 0x0000_0010;
    internal const uint FileAttributeNormal = 0x0000_0080;

    /// <summary>与 Win32 SHFILEINFO 布局一致</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    internal static extern IntPtr SHGetFileInfoW(
        string pszPath,
        uint dwFileAttributes,
        out SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    internal static extern IntPtr SHGetFileInfoW(
        IntPtr pidl,
        uint dwFileAttributes,
        out SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    internal const int ShilLarge = 0x0;
    internal const int ShilSmall = 0x1;
    internal const int ShilExtraLarge = 0x2;
    internal const int ShilSysSmall = 0x3;
    internal const int ShilJumbo = 0x4;
    internal const int IldTransparent = 0x1;

    [ComImport]
    [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IImageList
    {
        [PreserveSig]
        int Add(IntPtr hbmImage, IntPtr hbmMask, out int pi);

        [PreserveSig]
        int ReplaceIcon(int i, IntPtr hicon, out int pi);

        [PreserveSig]
        int SetOverlayImage(int iImage, int iOverlay);

        [PreserveSig]
        int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);

        [PreserveSig]
        int AddMasked(IntPtr hbmImage, int crMask, out int pi);

        [PreserveSig]
        int Draw(IntPtr pimldp);

        [PreserveSig]
        int Remove(int i);

        [PreserveSig]
        int GetIcon(int i, int flags, out IntPtr picon);
    }

    [DllImport("shell32.dll", SetLastError = false)]
    internal static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

    [DllImport("shell32.dll", SetLastError = false)]
    internal static extern int SHGetKnownFolderIDList(
        ref Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppidl);

    internal const uint ShgsiIcon = 0x0000_0100;
    internal const uint ShgsiSmallIcon = 0x0000_0001;
    internal const uint ShgsiLargeIcon = 0x0000_0000;

    /// <summary>SHGSI_SYSICONINDEX：只要系统图像列表里的索引</summary>
    internal const uint ShgsiSysIconIndex = 0x0000_4000;

    internal const uint SiidRecycleBin = 31;

    /// <summary>SIID_SHIELD：UAC 管理员盾牌图标</summary>
    internal const uint SiidShield = 77;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHSTOCKICONINFO
    {
        public uint cbSize;
        public IntPtr hIcon;
        public int iSysImageIndex;
        public int iIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szPath;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    internal static extern int SHGetStockIconInfo(uint siid, uint uFlags, ref SHSTOCKICONINFO psii);

    /// <summary>与 Win32 SIZE 布局一致</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE
    {
        public int cx;
        public int cy;

        public SIZE(int width, int height)
        {
            cx = width;
            cy = height;
        }
    }

    /// <summary>SIIGBF_* 标志</summary>
    internal const uint SiigbfResizeToFit = 0x0000_0000;
    internal const uint SiigbfBiggerSizeOk = 0x0000_0001;
    internal const uint SiigbfMemoryOnly = 0x0000_0002;
    internal const uint SiigbfIconOnly = 0x0000_0004;
    internal const uint SiigbfThumbnailOnly = 0x0000_0008;
    internal const uint SiigbfInCacheOnly = 0x0000_0010;
    internal const uint SiigbfCropToSquare = 0x0000_0020;
    internal const uint SiigbfWideThumbnails = 0x0000_0040;
    internal const uint SiigbfIconBackground = 0x0000_0080;

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItemImageFactory
    {
        void GetImage(SIZE size, uint flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    internal static extern void SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);
}
