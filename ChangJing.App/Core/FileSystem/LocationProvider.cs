using System.Runtime.InteropServices;
using ChangJing.App.Core.LongPath;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Core.FileSystem;

/// <summary>驱动器类型，对应 Win32 DRIVE_*</summary>
public enum DriveKind
{
    Unknown = 0,

    NoRootDirectory = 1,

    Removable = 2,

    Fixed = 3,

    Network = 4,

    CdRom = 5,

    RamDisk = 6,
}

public sealed class DriveItem
{
    public DriveItem(
        string rootPath,
        string? volumeLabel,
        string? fileSystem,
        DriveKind kind,
        bool isReady,
        ulong totalSizeBytes = 0,
        ulong freeSpaceBytes = 0)
    {
        RootPath = rootPath;
        VolumeLabel = volumeLabel;
        FileSystem = fileSystem;
        Kind = kind;
        IsReady = isReady;
        TotalSizeBytes = totalSizeBytes;
        FreeSpaceBytes = freeSpaceBytes;
    }

    /// <summary>根路径，形如 C:\</summary>
    public string RootPath { get; }

    /// <summary>卷标；未就绪或没有卷标时为 null</summary>
    public string? VolumeLabel { get; }

    /// <summary>文件系统名，如 NTFS / FAT32；未就绪时为 null</summary>
    public string? FileSystem { get; }

    public DriveKind Kind { get; }

    /// <summary>是否就绪，能读到卷信息</summary>
    public bool IsReady { get; }

    /// <summary>卷总容量，单位字节</summary>
    public ulong TotalSizeBytes { get; }

    /// <summary>卷可用空间，单位字节</summary>
    public ulong FreeSpaceBytes { get; }

    /// <summary>就绪且总容量大于 0 才算取到容量信息</summary>
    public bool HasSizeInfo => IsReady && TotalSizeBytes > 0;

    /// <summary>盘符，不含冒号，如 C</summary>
    public string DriveLetter => RootPath.Length > 0 ? RootPath.Substring(0, 1) : string.Empty;

    /// <summary>界面显示名：有卷标用卷标，否则用驱动器类型</summary>
    public string DisplayName
    {
        get
        {
            string letter = DriveLetter;
            if (!string.IsNullOrEmpty(VolumeLabel))
            {
                return $"{VolumeLabel} ({letter}:)";
            }

            return $"{KindText} ({letter}:)";
        }
    }

    /// <summary>驱动器类型的中文名</summary>
    public string KindText => Kind switch
    {
        DriveKind.Removable => "可移动磁盘",
        DriveKind.Fixed => "本地磁盘",
        DriveKind.Network => "网络驱动器",
        DriveKind.CdRom => "光盘驱动器",
        DriveKind.RamDisk => "RAM 磁盘",
        DriveKind.NoRootDirectory => "不可用",
        _ => "未知设备",
    };
}

/// <summary>一个已知文件夹，如桌面、文档、下载</summary>
public sealed class KnownFolderItem
{
    public KnownFolderItem(string id, string displayName, string? displayPath)
    {
        Id = id;
        DisplayName = displayName;
        DisplayPath = displayPath;
    }

    /// <summary>稳定标识，如 Desktop，供设置持久化</summary>
    public string Id { get; }

    /// <summary>界面显示名，中文，如「桌面」</summary>
    public string DisplayName { get; }

    /// <summary>普通路径；系统未提供时为 null</summary>
    public string? DisplayPath { get; }

    /// <summary>扩展路径；未提供时为 null</summary>
    public string? LongPath =>
        string.IsNullOrEmpty(DisplayPath) ? null : PathHelper.ToLong(DisplayPath!);

    public bool IsAvailable => !string.IsNullOrEmpty(DisplayPath);

    /// <inheritdoc/>
    public override string ToString() => $"{DisplayName} → {DisplayPath ?? "(不可用)"}";
}

/// <summary>提供驱动器列表与已知文件夹</summary>
public static class LocationProvider
{
    // 已知文件夹的 KNOWNFOLDERID
    private static readonly Guid FolderIdDesktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    private static readonly Guid FolderIdDocuments = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");
    private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");
    private static readonly Guid FolderIdPictures = new("33E28130-4E1E-4676-835A-98395C3BC3BB");
    private static readonly Guid FolderIdMusic = new("4BD8D571-6D19-48D3-BE97-422220080E43");
    private static readonly Guid FolderIdVideos = new("18989B1D-99B5-455B-841C-AB7C74E4DDFC");

    private static readonly object ProbeLock = new();

    public static IReadOnlyList<DriveItem> GetDrives()
    {
        var drives = new List<DriveItem>();

        string[] roots = GetLogicalDriveRoots();
        if (roots.Length == 0)
        {
            return drives;
        }

        lock (ProbeLock)
        {
            uint previousMode = NativeMethods.SetErrorMode(NativeMethods.SemFailCriticalErrors);
            try
            {
                foreach (string root in roots)
                {
                    drives.Add(ProbeDrive(root));
                }
            }
            catch (Exception ex)
            {
                LogManager.Error(typeof(LocationProvider), ex);
            }
            finally
            {
                NativeMethods.SetErrorMode(previousMode);
            }
        }

        return drives;
    }

    /// <summary>取标准已知文件夹，顺序固定；系统未提供的项路径为 null</summary>
    public static IReadOnlyList<KnownFolderItem> GetKnownFolders()
    {
        return new[]
        {
            CreateKnownFolder("Desktop", "桌面", FolderIdDesktop),
            CreateKnownFolder("Documents", "文档", FolderIdDocuments),
            CreateKnownFolder("Downloads", "下载", FolderIdDownloads),
            CreateKnownFolder("Pictures", "图片", FolderIdPictures),
            CreateKnownFolder("Music", "音乐", FolderIdMusic),
            CreateKnownFolder("Videos", "视频", FolderIdVideos),
        };
    }

    /// <summary>把已知文件夹的显示名映射为路径</summary>
    public static string? ResolveAlias(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        foreach (KnownFolderItem item in GetKnownFolders())
        {
            if (string.Equals(item.DisplayName, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Id, name, StringComparison.OrdinalIgnoreCase))
            {
                return item.DisplayPath;
            }
        }

        return null;
    }

    private static DriveItem ProbeDrive(string root)
    {
        uint typeValue = NativeMethods.GetDriveTypeW(root);
        var kind = (DriveKind)typeValue;

        var volumeName = new char[261];
        var fileSystemName = new char[261];

        bool ready = NativeMethods.GetVolumeInformationW(
            root,
            volumeName,
            (uint)volumeName.Length,
            out _,
            out _,
            out _,
            fileSystemName,
            (uint)fileSystemName.Length);

        string? label = null;
        string? fileSystem = null;

        if (ready)
        {
            string volume = new string(volumeName).TrimEnd('\0');
            string fs = new string(fileSystemName).TrimEnd('\0');
            label = volume.Length == 0 ? null : volume;
            fileSystem = fs.Length == 0 ? null : fs;
        }

        ulong totalSize = 0;
        ulong freeSpace = 0;
        if (ready && NativeMethods.GetDiskFreeSpaceExW(root, out ulong freeToCaller, out ulong total, out _))
        {
            totalSize = total;
            freeSpace = freeToCaller;
        }

        return new DriveItem(
            NormalizeRoot(root),
            label,
            fileSystem,
            kind,
            ready,
            totalSize,
            freeSpace);
    }

    private static KnownFolderItem CreateKnownFolder(string id, string displayName, Guid folderId)
    {
        string? path = GetKnownFolderPath(folderId);
        if (path is not null)
        {
            path = PathHelper.ToDisplay(path);
        }

        return new KnownFolderItem(id, displayName, path);
    }

    private static string? GetKnownFolderPath(Guid folderId)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            int hr = NativeMethods.SHGetKnownFolderPath(folderId, 0, IntPtr.Zero, out buffer);
            if (hr != 0 || buffer == IntPtr.Zero)
            {
                LogManager.Warning(typeof(LocationProvider), $"取已知文件夹路径失败：{folderId}（HRESULT 0x{hr:X8}）");
                return null;
            }

            return Marshal.PtrToStringUni(buffer);
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(LocationProvider), ex);
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                NativeMethods.CoTaskMemFree(buffer);
            }
        }
    }

    private static string[] GetLogicalDriveRoots()
    {
        try
        {
            uint length = NativeMethods.GetLogicalDriveStringsW(0, null);
            if (length == 0)
            {
                return Array.Empty<string>();
            }

            var buffer = new char[length + 1];
            uint written = NativeMethods.GetLogicalDriveStringsW((uint)buffer.Length, buffer);
            if (written == 0)
            {
                return Array.Empty<string>();
            }

            return new string(buffer, 0, (int)written)
                .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(LocationProvider), ex);
            return Array.Empty<string>();
        }
    }

    /// <summary>把 c:\ 统一成 C:\</summary>
    private static string NormalizeRoot(string root)
    {
        if (root.Length >= 2 && char.IsLetter(root[0]) && root[1] == ':')
        {
            return char.ToUpperInvariant(root[0]) + ":\\";
        }

        return root;
    }
}
