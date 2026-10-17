using System.IO;

namespace ChangJing.App.Services;

/// <summary>%LOCALAPPDATA%\ChangJing 下的固定路径</summary>
public static class AppPaths
{
    /// <summary>产品数据根目录：%LOCALAPPDATA%\ChangJing</summary>
    public static string AppDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChangJing");

    /// <summary>日志目录：%LOCALAPPDATA%\ChangJing\logs</summary>
    public static string LogsDirectory { get; } = Path.Combine(AppDataDirectory, "logs");

    /// <summary>设置文件：%LOCALAPPDATA%\ChangJing\settings.json</summary>
    public static string SettingsFilePath { get; } = Path.Combine(AppDataDirectory, "settings.json");

    /// <summary>外部打开用的短根目录：%LOCALAPPDATA%\ChangJing\o，建目录联接到目标文件夹</summary>
    public static string OpenShortRootDirectory { get; } = Path.Combine(AppDataDirectory, "o");

    /// <summary>临时副本根目录：%LOCALAPPDATA%\ChangJing\tmp，联接不可用时用</summary>
    public static string TempCopyRootDirectory { get; } = Path.Combine(AppDataDirectory, "tmp");

    /// <summary>PDF 预览副本目录：%LOCALAPPDATA%\ChangJing\preview</summary>
    // 不能放 tmp 下：TempOpenCopy.SweepResidue 启动时会扫掉 tmp 的子目录
    public static string PreviewTempDirectory { get; } = Path.Combine(AppDataDirectory, "preview");
}
