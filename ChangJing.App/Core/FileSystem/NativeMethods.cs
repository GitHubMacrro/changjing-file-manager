using System.Runtime.InteropServices;
using ChangJing.App.Services;
using Microsoft.Win32.SafeHandles;

namespace ChangJing.App.Core.FileSystem;

/// <summary>本模块用到的 Win32 P/Invoke</summary>
// 每个按路径解析文件的函数都包一层 Wow64FileSystemRedirection 作用域：
// x86 进程下临时关闭 WOW64 重定向，作用域内只有这一个 Win32 调用，返回后立即还原
internal static class NativeMethods
{
    /// <summary>FindFirstFileEx 的 fInfoLevelId：只取基本字段，更快</summary>
    internal const int FindExInfoBasic = 1;

    /// <summary>FindFirstFileEx 的 fInfoLevelId：取完整字段，含短名</summary>
    internal const int FindExInfoStandard = 0;

    internal const int FindExSearchNameMatch = 0;

    /// <summary>FindFirstFileEx 的 dwSearchFilter：按目录匹配，不推荐</summary>
    internal const int FindExSearchLimitToDirectories = 1;

    /// <summary>FindFirstFileEx 标志：大缓冲区批量返回，网络与慢盘上更快</summary>
    internal const int FindFirstExLargeFetch = 0x0000_0002;

    /// <summary>FindFirstFileEx 标志：区分大小写匹配，仅 NTFS 支持</summary>
    internal const int FindFirstExCaseSensitive = 0x0000_0001;

    // 文件属性
    internal const uint FileAttributeReadOnly = 0x0000_0001;
    internal const uint FileAttributeHidden = 0x0000_0002;
    internal const uint FileAttributeSystem = 0x0000_0004;
    internal const uint FileAttributeDirectory = 0x0000_0010;
    internal const uint FileAttributeArchive = 0x0000_0020;
    internal const uint FileAttributeDevice = 0x0000_0040;
    internal const uint FileAttributeNormal = 0x0000_0080;
    internal const uint FileAttributeTemporary = 0x0000_0100;
    internal const uint FileAttributeSparseFile = 0x0000_0200;
    internal const uint FileAttributeReparsePoint = 0x0000_0400;
    internal const uint FileAttributeCompressed = 0x0000_0800;
    internal const uint FileAttributeOffline = 0x0000_1000;
    internal const uint FileAttributeNotContentIndexed = 0x0000_2000;
    internal const uint FileAttributeEncrypted = 0x0000_4000;
    internal const uint FileAttributeIntegrityStream = 0x0000_8000;
    internal const uint FileAttributeVirtual = 0x0001_0000;
    internal const uint FileAttributeNoScrubData = 0x0002_0000;
    internal const uint FileAttributeRecallOnOpen = 0x0004_0000;
    internal const uint FileAttributePinned = 0x0008_0000;
    internal const uint FileAttributeUnpinned = 0x0010_0000;

    // 驱动器类型
    internal const uint DriveUnknown = 0;
    internal const uint DriveNoRootDir = 1;
    internal const uint DriveRemovable = 2;
    internal const uint DriveFixed = 3;
    internal const uint DriveRemote = 4;
    internal const uint DriveCdRom = 5;
    internal const uint DriveRamDisk = 6;

    // 卷信息标志
    internal const uint FileVolumeIsCompressed = 0x0000_8000;

    /// <summary>与 Win32 FILETIME 布局一致</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    /// <summary>与 Win32 WIN32_FIND_DATAW 布局一致</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;

        /// <summary>把高 / 低两段合成 64 位大小</summary>
        public readonly ulong Size => ((ulong)nFileSizeHigh << 32) | nFileSizeLow;

        /// <summary>修改时间，UTC；无法表示时为 MinValue</summary>
        public readonly DateTime LastWriteTimeUtc => ToDateTimeUtc(ftLastWriteTime);
    }

    /// <summary>把 Win32 FILETIME 转成 UTC DateTime；越界时为 MinValue</summary>
    internal static DateTime ToDateTimeUtc(FILETIME fileTime)
    {
        long value = ((long)fileTime.dwHighDateTime << 32) | fileTime.dwLowDateTime;
        if (value <= 0 || value > DateTime.MaxValue.Ticks)
        {
            return DateTime.MinValue;
        }

        return DateTime.FromFileTimeUtc(value);
    }

    /// <summary>查找文件 / 目录；x86 下临时关闭 WOW64 重定向，确保看到真实 System32</summary>
    // 枚举句柄在关闭重定向期间创建，之后还原也不影响该句柄已解析的目录视图
    internal static SafeFindHandle FindFirstFileExW(
        string lpFileName,
        int fInfoLevelId,
        out WIN32_FIND_DATAW lpFindFileData,
        int fSearchOp,
        IntPtr lpSearchFilter,
        int dwAdditionalFlags)
    {
        using Wow64FileSystemRedirection.Scope _ = Wow64FileSystemRedirection.Suspend();
        return FindFirstFileExWRaw(
            lpFileName,
            fInfoLevelId,
            out lpFindFileData,
            fSearchOp,
            lpSearchFilter,
            dwAdditionalFlags);
    }

    [DllImport("kernel32.dll", EntryPoint = "FindFirstFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFindHandle FindFirstFileExWRaw(
        string lpFileName,
        int fInfoLevelId,
        out WIN32_FIND_DATAW lpFindFileData,
        int fSearchOp,
        IntPtr lpSearchFilter,
        int dwAdditionalFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FindNextFileW(SafeFindHandle hFindFile, out WIN32_FIND_DATAW lpFindFileData);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FindClose(IntPtr hFindFile);

    internal const uint MoveFileReplaceExisting = 0x0000_0001;

    /// <summary>MOVEFILE_COPY_ALLOWED：跨卷时复制 + 删除</summary>
    internal const uint MoveFileCopyAllowed = 0x0000_0002;

    /// <summary>MOVEFILE_DELAY_UNTIL_REBOOT：延迟到重启后执行，本工具未使用</summary>
    internal const uint MoveFileDelayUntilReboot = 0x0000_0004;

    /// <summary>MOVEFILE_WRITE_THROUGH：等到移动真正落盘再返回</summary>
    internal const uint MoveFileWriteThrough = 0x0000_0008;

    /// <summary>创建目录；必须传扩展路径，否则结尾的点 / 空格会被裁剪</summary>
    internal static bool CreateDirectoryW(string lpPathName, IntPtr lpSecurityAttributes)
    {
        using Wow64FileSystemRedirection.Scope _ = Wow64FileSystemRedirection.Suspend();
        return CreateDirectoryWRaw(lpPathName, lpSecurityAttributes);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryWRaw(string lpPathName, IntPtr lpSecurityAttributes);

    /// <summary>移动 / 重命名文件或目录；新名与源必须同为扩展路径</summary>
    internal static bool MoveFileExW(string lpExistingFileName, string lpNewFileName, uint dwFlags)
    {
        using Wow64FileSystemRedirection.Scope _ = Wow64FileSystemRedirection.Suspend();
        return MoveFileExWRaw(lpExistingFileName, lpNewFileName, dwFlags);
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExWRaw(string lpExistingFileName, string lpNewFileName, uint dwFlags);

    /// <summary>与 Win32 WIN32_FILE_ATTRIBUTE_DATA 布局一致</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WIN32_FILE_ATTRIBUTE_DATA
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;

        /// <summary>把高 / 低两段合成 64 位大小</summary>
        public readonly ulong Size => ((ulong)nFileSizeHigh << 32) | nFileSizeLow;

        /// <summary>创建时间，UTC</summary>
        public readonly DateTime CreationTimeUtc => ToDateTimeUtc(ftCreationTime);

        /// <summary>最后访问时间，UTC</summary>
        public readonly DateTime LastAccessTimeUtc => ToDateTimeUtc(ftLastAccessTime);

        /// <summary>修改时间，UTC</summary>
        public readonly DateTime LastWriteTimeUtc => ToDateTimeUtc(ftLastWriteTime);
    }

    /// <summary>GetFileAttributesEx 的 fInfoLevelId：标准信息，属性 + 时间 + 大小</summary>
    internal const int GetFileExInfoStandard = 0;

    /// <summary>查询单条路径的属性 / 时间 / 大小</summary>
    internal static bool GetFileAttributesExW(
        string lpFileName,
        int fInfoLevelId,
        out WIN32_FILE_ATTRIBUTE_DATA lpFileInformation)
    {
        using Wow64FileSystemRedirection.Scope _ = Wow64FileSystemRedirection.Suspend();
        return GetFileAttributesExWRaw(lpFileName, fInfoLevelId, out lpFileInformation);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileAttributesExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileAttributesExWRaw(
        string lpFileName,
        int fInfoLevelId,
        out WIN32_FILE_ATTRIBUTE_DATA lpFileInformation);

    internal static bool RemoveDirectoryW(string lpPathName)
    {
        using Wow64FileSystemRedirection.Scope _ = Wow64FileSystemRedirection.Suspend();
        return RemoveDirectoryWRaw(lpPathName);
    }

    [DllImport("kernel32.dll", EntryPoint = "RemoveDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveDirectoryWRaw(string lpPathName);

    internal static bool DeleteFileW(string lpFileName)
    {
        using Wow64FileSystemRedirection.Scope _ = Wow64FileSystemRedirection.Suspend();
        return DeleteFileWRaw(lpFileName);
    }

    [DllImport("kernel32.dll", EntryPoint = "DeleteFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteFileWRaw(string lpFileName);

    internal const uint CopyFileFailIfExists = 0x0000_0001;

    /// <summary>CopyFileExW 的 COPY_FILE_RESTARTABLE：可断点续传，本工具未使用</summary>
    internal const uint CopyFileRestartable = 0x0000_0002;

    internal const uint CopyFileAllowDecryptedDestination = 0x0000_0008;

    /// <summary>CopyFileExW 的 COPY_FILE_COPY_SYMLINK：复制符号链接本身而非目标</summary>
    internal const uint CopyFileCopySymlink = 0x0000_0800;

    internal const uint ProgressContinue = 0;

    /// <summary>CopyProgressRoutine 的返回值：取消，删除未完成的目标</summary>
    internal const uint ProgressCancel = 1;

    /// <summary>CopyProgressRoutine 的返回值：停止，目标保留部分内容</summary>
    internal const uint ProgressStop = 2;

    /// <summary>复制进度回调，参数与 Win32 LPPROGRESS_ROUTINE 一致</summary>
    internal delegate uint CopyProgressRoutine(
        long totalFileSize,
        long totalBytesTransferred,
        long streamSize,
        long streamBytesTransferred,
        uint streamNumber,
        uint callbackReason,
        IntPtr sourceFile,
        IntPtr destinationFile,
        IntPtr data);

    /// <summary>复制文件，带进度回调、可取消；x86 下临时关闭 WOW64 重定向</summary>
    // 进度回调只读进度 / 查暂停标志，不碰 Shell/COM，故整个复制期处于作用域内是安全的
    internal static bool CopyFileExW(
        string lpExistingFileName,
        string lpNewFileName,
        CopyProgressRoutine? lpProgressRoutine,
        IntPtr lpData,
        ref int pbCancel,
        uint dwCopyFlags)
    {
        using Wow64FileSystemRedirection.Scope _ = Wow64FileSystemRedirection.Suspend();
        return CopyFileExWRaw(lpExistingFileName, lpNewFileName, lpProgressRoutine, lpData, ref pbCancel, dwCopyFlags);
    }

    [DllImport("kernel32.dll", EntryPoint = "CopyFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CopyFileExWRaw(
        string lpExistingFileName,
        string lpNewFileName,
        CopyProgressRoutine? lpProgressRoutine,
        IntPtr lpData,
        ref int pbCancel,
        uint dwCopyFlags);

    /// <summary>查询文件属性；失败返回 InvalidFileAttributes</summary>
    internal static uint GetFileAttributesW(string lpFileName)
    {
        using Wow64FileSystemRedirection.Scope _ = Wow64FileSystemRedirection.Suspend();
        return GetFileAttributesWRaw(lpFileName);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileAttributesW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesWRaw(string lpFileName);

    internal static bool SetFileAttributesW(string lpFileName, uint dwFileAttributes)
    {
        using Wow64FileSystemRedirection.Scope _ = Wow64FileSystemRedirection.Suspend();
        return SetFileAttributesWRaw(lpFileName, dwFileAttributes);
    }

    [DllImport("kernel32.dll", EntryPoint = "SetFileAttributesW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileAttributesWRaw(string lpFileName, uint dwFileAttributes);

    // CreateFileW 的访问权限 / 共享 / 处置 / 标志
    internal const uint GenericRead = 0x8000_0000;
    internal const uint GenericWrite = 0x4000_0000;
    internal const uint FileWriteAttributes = 0x0000_0100;
    internal const uint FileShareRead = 0x0000_0001;
    internal const uint FileShareWrite = 0x0000_0002;
    internal const uint FileShareDelete = 0x0000_0004;
    internal const uint OpenExisting = 3;
    internal const uint FileFlagBackupSemantics = 0x0200_0000;
    internal const uint FileFlagOpenReparsePoint = 0x0020_0000;

    /// <summary>创建 / 打开文件或目录，取时间戳用的句柄</summary>
    internal static SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile)
    {
        using Wow64FileSystemRedirection.Scope _ = Wow64FileSystemRedirection.Suspend();
        return CreateFileWRaw(
            lpFileName,
            dwDesiredAccess,
            dwShareMode,
            lpSecurityAttributes,
            dwCreationDisposition,
            dwFlagsAndAttributes,
            hTemplateFile);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileWRaw(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    /// <summary>设置文件 / 目录的创建、访问、修改时间；传 IntPtr.Zero 表示不改该项</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetFileTime(
        SafeFileHandle hFile,
        IntPtr lpCreationTime,
        IntPtr lpLastAccessTime,
        IntPtr lpLastWriteTime);

    /// <summary>用一个句柄设置时间戳；为 MinValue 的项不改</summary>
    internal static bool SetFileTimes(
        SafeFileHandle handle,
        DateTime creationUtc,
        DateTime accessUtc,
        DateTime writeUtc)
    {
        IntPtr creation = IntPtr.Zero;
        IntPtr access = IntPtr.Zero;
        IntPtr write = IntPtr.Zero;

        try
        {
            if (creationUtc != DateTime.MinValue)
            {
                creation = AllocFileTime(creationUtc);
            }

            if (accessUtc != DateTime.MinValue)
            {
                access = AllocFileTime(accessUtc);
            }

            if (writeUtc != DateTime.MinValue)
            {
                write = AllocFileTime(writeUtc);
            }

            return SetFileTime(handle, creation, access, write);
        }
        finally
        {
            if (creation != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(creation);
            }

            if (access != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(access);
            }

            if (write != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(write);
            }
        }
    }

    private static IntPtr AllocFileTime(DateTime utc)
    {
        long value = utc.ToFileTimeUtc();
        IntPtr pointer = Marshal.AllocHGlobal(sizeof(long));
        Marshal.WriteInt64(pointer, value);
        return pointer;
    }

    /// <summary>取包含指定路径的卷挂载点，如 C:\、\\?\Volume{...}</summary>
    internal static bool GetVolumePathNameW(string lpszFileName, char[] lpszVolumePathName, uint cchBufferLength)
    {
        using Wow64FileSystemRedirection.Scope _ = Wow64FileSystemRedirection.Suspend();
        return GetVolumePathNameWRaw(lpszFileName, lpszVolumePathName, cchBufferLength);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameWRaw(string lpszFileName, char[] lpszVolumePathName, uint cchBufferLength);

    /// <summary>无效的 FILE_ATTRIBUTE，查询失败的约定值 0xFFFFFFFF</summary>
    internal const uint InvalidFileAttributes = 0xFFFF_FFFF;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint GetLogicalDriveStringsW(uint nBufferLength, char[]? lpBuffer);

    internal static uint GetDriveTypeW(string lpRootPathName)
    {
        using Wow64FileSystemRedirection.Scope _ = Wow64FileSystemRedirection.Suspend();
        return GetDriveTypeWRaw(lpRootPathName);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetDriveTypeWRaw(string lpRootPathName);

    internal static bool GetVolumeInformationW(
        string lpRootPathName,
        char[]? lpVolumeNameBuffer,
        uint nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        char[]? lpFileSystemNameBuffer,
        uint nFileSystemNameSize)
    {
        using Wow64FileSystemRedirection.Scope _ = Wow64FileSystemRedirection.Suspend();
        return GetVolumeInformationWRaw(
            lpRootPathName,
            lpVolumeNameBuffer,
            nVolumeNameSize,
            out lpVolumeSerialNumber,
            out lpMaximumComponentLength,
            out lpFileSystemFlags,
            lpFileSystemNameBuffer,
            nFileSystemNameSize);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationWRaw(
        string lpRootPathName,
        char[]? lpVolumeNameBuffer,
        uint nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        char[]? lpFileSystemNameBuffer,
        uint nFileSystemNameSize);

    /// <summary>查询卷的可用 / 总空间，不需要权限</summary>
    internal static bool GetDiskFreeSpaceExW(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes)
    {
        using Wow64FileSystemRedirection.Scope _ = Wow64FileSystemRedirection.Suspend();
        return GetDiskFreeSpaceExWRaw(
            lpDirectoryName,
            out lpFreeBytesAvailableToCaller,
            out lpTotalNumberOfBytes,
            out lpTotalNumberOfFreeBytes);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExWRaw(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);

    /// <summary>不显示「请插入磁盘」等系统错误对话框</summary>
    internal const uint SemFailCriticalErrors = 0x0001;

    [DllImport("kernel32.dll", SetLastError = false)]
    internal static extern uint SetErrorMode(uint uMode);

    internal const int WmClipboardUpdate = 0x031D;

    /// <summary>剪贴板序列号：每次剪贴板内容被设置时递增</summary>
    [DllImport("user32.dll", SetLastError = false)]
    internal static extern uint GetClipboardSequenceNumber();

    /// <summary>注册剪贴板变化通知，收到 WM_CLIPBOARDUPDATE</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    // 写文本走 Win32 可控重试耗时，OLE 会长时间重试卡界面
    internal const uint CfUnicodeText = 13;
    internal const uint GmemMoveable = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalUnlock(IntPtr hMem);

    internal const int WmDeviceChange = 0x0219;

    internal const int DbtDeviceArrival = 0x8000;

    internal const int DbtDeviceRemoveComplete = 0x8004;

    /// <summary>DBT_DEVTYP_VOLUME：逻辑卷，含盘符</summary>
    internal const int DbtDevTypVolume = 0x0002;

    /// <summary>DEV_BROADCAST_HDR：设备变更广播的公共头</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DEV_BROADCAST_HDR
    {
        public uint dbch_size;
        public uint dbch_devicetype;
        public uint dbch_reserved;
    }

    /// <summary>DEV_BROADCAST_VOLUME：卷变更广播，dbcv_unitmask 是盘符位掩码</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DEV_BROADCAST_VOLUME
    {
        public uint dbch_size;
        public uint dbch_devicetype;
        public uint dbch_reserved;
        public uint dbcv_unitmask;
        public ushort dbcv_flags;
    }

    // DefineDosDeviceW 只用于清除旧版本残留的盘符映射
    /// <summary>DDD_REMOVE_DEFINITION：移除映射，仅迁移旧版本残留用</summary>
    internal const uint DddRemoveDefinition = 0x0000_0002;

    /// <summary>DDD_EXACT_MATCH_ON_REMOVE：移除时要求目标路径完全匹配，仅迁移旧版本残留用</summary>
    internal const uint DddExactMatchOnRemove = 0x0000_0004;

    /// <summary>移除盘符映射，当前用户无需管理员；仅迁移旧版本残留</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DefineDosDeviceW(uint dwFlags, string lpDeviceName, string lpTargetPath);

    /// <summary>逻辑驱动器位掩码，bit0 = A:，bit25 = Z:</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint GetLogicalDrives();

    internal const uint IoReparseTagMountPoint = 0xA000_0003;

    internal const uint FsctlSetReparsePoint = 0x0009_00A4;

    /// <summary>设备控制，用于 FSCTL_SET_REPARSE_POINT 建立目录联接</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[] lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    internal const uint SymbolicLinkFlagDirectory = 0x0000_0001;

    /// <summary>创建符号链接，目录联接失败时备选，通常需管理员</summary>
    internal static bool CreateSymbolicLinkW(string lpSymlinkFileName, string lpTargetFileName, uint dwFlags)
    {
        using Wow64FileSystemRedirection.Scope _ = Wow64FileSystemRedirection.Suspend();
        return CreateSymbolicLinkWRaw(lpSymlinkFileName, lpTargetFileName, dwFlags);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateSymbolicLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateSymbolicLinkWRaw(string lpSymlinkFileName, string lpTargetFileName, uint dwFlags);

    internal const int KfFlagDontVerify = 0x0000_4000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = false)]
    internal static extern int SHGetKnownFolderPath(
        in Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);

    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern void CoTaskMemFree(IntPtr pv);
}

/// <summary>枚举句柄包装：保证 FindClose 恰好调用一次</summary>
internal sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeFindHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => NativeMethods.FindClose(handle);
}

/// <summary>Win32 错误码 → 中文提示</summary>
internal static class Win32ErrorMessages
{
    internal const int ErrorFileNotFound = 2;
    internal const int ErrorPathNotFound = 3;
    internal const int ErrorAccessDenied = 5;
    internal const int ErrorInvalidDrive = 15;
    internal const int ErrorNotReady = 21;
    internal const int ErrorSharingViolation = 32;
    internal const int ErrorFileExists = 80;
    internal const int ErrorInvalidName = 123;
    internal const int ErrorMoreData = 234;
    internal const int ErrorNoMoreFiles = 18;
    internal const int ErrorInvalidParameter = 87;
    internal const int ErrorDirectory = 267;
    internal const int ErrorWriteProtect = 19;
    internal const int ErrorFileNameExcedRange = 206;
    internal const int ErrorDirectoryNotEmpty = 145;
    internal const int ErrorAlreadyExists = 183;
    internal const int ErrorDiskFull = 112;
    internal const int ErrorWriteFault = 29;
    internal const int ErrorLockViolation = 33;

    /// <summary>错误码是否表示「没有更多文件」，即枚举正常结束</summary>
    internal static bool IsEndOfEnumeration(int errorCode)
        => errorCode == ErrorNoMoreFiles || errorCode == ErrorMoreData;

    /// <summary>把错误码翻译成中文提示；未知错误码带上原始数字</summary>
    internal static string Describe(int errorCode)
    {
        return errorCode switch
        {
            ErrorFileNotFound => "找不到指定的文件或文件夹。",
            ErrorPathNotFound => "找不到指定的路径，请检查路径是否正确。",
            ErrorAccessDenied => "没有权限访问此文件或文件夹。",
            ErrorInvalidDrive => "驱动器不存在。",
            ErrorNotReady => "设备未就绪（可能未插入磁盘或光驱内无光盘）。",
            ErrorSharingViolation => "文件正被其他程序使用。",
            ErrorFileExists => "目标位置已存在同名文件或文件夹。",
            ErrorInvalidName => "文件名或文件夹名无效（可能包含非法字符或名称过长）。",
            ErrorNoMoreFiles => "没有更多文件。",
            ErrorMoreData => "没有更多文件。",
            ErrorInvalidParameter => "参数无效（路径可能过长或包含非法字符）。",
            ErrorDirectory => "目标是一个文件夹。",
            ErrorWriteProtect => "介质已写保护。",
            ErrorFileNameExcedRange => "文件名过长，超出文件系统上限。",
            ErrorDirectoryNotEmpty => "文件夹不为空。",
            ErrorAlreadyExists => "目标位置已存在同名文件或文件夹。",
            ErrorDiskFull => "磁盘空间不足。",
            _ => $"操作失败（错误码 {errorCode}）。",
        };
    }
}
