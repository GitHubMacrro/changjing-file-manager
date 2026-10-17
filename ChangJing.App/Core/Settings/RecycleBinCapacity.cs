using ChangJing.App.Core.RecycleBin;

namespace ChangJing.App.Core.Settings;

/// <summary>回收站容量设置：把百分比 / 固定 GB 二选一应用到 RecycleBinOptions 即时生效</summary>
public static class RecycleBinCapacity
{
    /// <summary>1 GB 的字节数</summary>
    public const long BytesPerGigabyte = 1024L * 1024 * 1024;

    /// <summary>容量模式是否为「固定 GB」</summary>
    public static bool IsFixedGigabyteMode(string? mode)
        => string.Equals(mode, AppSettings.CapacityModeFixedGigabyte, StringComparison.OrdinalIgnoreCase);

    /// <summary>把设置里的容量选项应用到回收站选项，即时生效</summary>
    public static void Apply(AppSettings settings, RecycleBinOptions options)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);

        if (IsFixedGigabyteMode(settings.RecycleBinCapacityMode))
        {
            options.CapacityFixedBytes = GigabytesToBytes(settings.RecycleBinCapacityGigabytes);
        }
        else
        {
            options.CapacityFixedBytes = 0;
            options.CapacityPercent = Math.Clamp(
                settings.RecycleBinCapacityPercent,
                AppSettings.MinRecycleBinCapacityPercent,
                AppSettings.MaxRecycleBinCapacityPercent);
        }
    }

    /// <summary>把 GB 转成字节，夹在允许范围内，避免 0 / 溢出</summary>
    public static long GigabytesToBytes(double gigabytes)
    {
        double clamped = Math.Clamp(
            gigabytes,
            AppSettings.MinRecycleBinCapacityGigabytes,
            AppSettings.MaxRecycleBinCapacityGigabytes);

        return (long)Math.Round(clamped * BytesPerGigabyte);
    }

    /// <summary>把字节数转成 GB 文本，设置对话框回显用</summary>
    public static double BytesToGigabytes(long bytes)
        => bytes <= 0 ? 0 : bytes / (double)BytesPerGigabyte;
}
