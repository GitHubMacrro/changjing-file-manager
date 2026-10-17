using System.Windows.Media;
using ChangJing.App.Core.FileSystem;
using ChangJing.App.Services;

namespace ChangJing.App.ViewModels;

/// <summary>「此电脑」页面上部「文件夹」分组的一项</summary>
public sealed class ThisPcFolderViewModel
{
    public ThisPcFolderViewModel(KnownFolderItem knownFolder)
    {
        ArgumentNullException.ThrowIfNull(knownFolder);

        Name = knownFolder.DisplayName;
        DisplayPath = knownFolder.DisplayPath ?? string.Empty;
        IsAvailable = knownFolder.IsAvailable;
        Icon = SystemIconService.GetKnownFolderIcon(knownFolder.Id, 48);
    }

    public string Name { get; }

    /// <summary>普通路径；不可用时为空串</summary>
    public string DisplayPath { get; }

    public bool IsAvailable { get; }

    /// <summary>系统图标 48px</summary>
    public ImageSource? Icon { get; }
}

/// <summary>「此电脑」页面下部「设备和驱动器」分组的一项</summary>
public sealed class ThisPcDriveViewModel
{
    /// <summary>可用空间低于该比例时进度条变红</summary>
    public const double LowSpaceThreshold = 0.10;

    public ThisPcDriveViewModel(DriveItem drive)
    {
        ArgumentNullException.ThrowIfNull(drive);

        DisplayName = drive.DisplayName;
        RootPath = drive.RootPath;
        IsReady = drive.IsReady;
        HasSizeInfo = drive.HasSizeInfo;
        Icon = SystemIconService.GetDriveIconForPath(drive.RootPath, drive.IsReady, 48);

        if (drive.HasSizeInfo)
        {
            double total = drive.TotalSizeBytes;
            double free = drive.FreeSpaceBytes;
            ProgressPercent = total > 0 ? Math.Clamp((total - free) / total * 100.0, 0, 100) : 0;
            IsLowSpace = total > 0 && free / total < LowSpaceThreshold;
            CapacityText = $"{FileItemViewModel.FormatSize(drive.FreeSpaceBytes)} 可用，共 {FileItemViewModel.FormatSize(drive.TotalSizeBytes)}";
        }
        else
        {
            ProgressPercent = 0;
            IsLowSpace = false;
            CapacityText = string.Empty;
        }
    }

    public string DisplayName { get; }

    public string RootPath { get; }

    public bool IsReady { get; }

    public bool HasSizeInfo { get; }

    /// <summary>已用空间百分比 0~100</summary>
    public double ProgressPercent { get; }

    /// <summary>可用空间是否低于 10%</summary>
    public bool IsLowSpace { get; }

    /// <summary>「X GB 可用，共 Y GB」；无容量信息时为空串</summary>
    public string CapacityText { get; }

    /// <summary>驱动器大图标 48px</summary>
    public ImageSource? Icon { get; }
}
