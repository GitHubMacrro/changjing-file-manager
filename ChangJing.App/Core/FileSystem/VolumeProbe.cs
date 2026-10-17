using ChangJing.App.Core.LongPath;
using ChangJing.App.Core.Operations;

namespace ChangJing.App.Core.FileSystem;

/// <summary>卷信息探测的对外门面：驱动器类型、挂载点、容量</summary>
public static class VolumeProbe
{
    /// <summary>取路径所在卷的挂载点，普通路径；失败返回空串</summary>
    public static string GetMountRoot(string displayPath)
    {
        try
        {
            return TransferVolume.GetMountRoot(PathHelper.ToLong(displayPath));
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>取卷的驱动器类型；查询失败返回 Unknown</summary>
    public static DriveKind GetDriveKind(string volumeRoot)
    {
        if (string.IsNullOrEmpty(volumeRoot))
        {
            return DriveKind.Unknown;
        }

        try
        {
            uint value = NativeMethods.GetDriveTypeW(volumeRoot);
            return value is > 6 ? DriveKind.Unknown : (DriveKind)value;
        }
        catch
        {
            return DriveKind.Unknown;
        }
    }

    /// <summary>该卷是否应「不进回收站」，可移动 / 网络盘</summary>
    public static bool IsRecycleBinExcluded(DriveKind kind)
        => kind is DriveKind.Removable or DriveKind.Network or DriveKind.CdRom or DriveKind.NoRootDirectory;

    /// <summary>盘符，不含冒号，如 C；UNC 或无法识别时返回空串</summary>
    public static string GetDriveLetter(string volumeRoot)
        => volumeRoot.Length >= 2 && volumeRoot[1] == ':' ? volumeRoot.Substring(0, 1) : string.Empty;

    /// <summary>当前逻辑驱动器位掩码，bit0 = A:</summary>
    public static uint GetLogicalDriveMask() => NativeMethods.GetLogicalDrives();

    /// <summary>把盘符位掩码 bit0 = A: 转成「E: F:」形式的文字</summary>
    public static string DescribeDriveLetters(uint mask)
    {
        var builder = new System.Text.StringBuilder(8);
        for (int i = 0; i < 26; i++)
        {
            if ((mask & (1u << i)) == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append((char)('A' + i)).Append(':');
        }

        return builder.Length == 0 ? "（未知盘符）" : builder.ToString();
    }

    /// <summary>取卷总容量，单位字节；失败返回 0</summary>
    public static long GetTotalBytes(string volumeRoot)
    {
        if (string.IsNullOrEmpty(volumeRoot))
        {
            return 0;
        }

        try
        {
            if (NativeMethods.GetDiskFreeSpaceExW(volumeRoot, out _, out ulong total, out _))
            {
                return total > long.MaxValue ? long.MaxValue : (long)total;
            }
        }
        catch
        {
        }

        return 0;
    }

    /// <summary>取卷可用空间，单位字节；失败返回 0</summary>
    public static long GetFreeBytes(string volumeRoot)
    {
        if (string.IsNullOrEmpty(volumeRoot))
        {
            return 0;
        }

        try
        {
            if (NativeMethods.GetDiskFreeSpaceExW(volumeRoot, out ulong free, out _, out _))
            {
                return free > long.MaxValue ? long.MaxValue : (long)free;
            }
        }
        catch
        {
        }

        return 0;
    }
}
