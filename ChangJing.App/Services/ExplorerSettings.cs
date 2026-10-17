using CustomWin.Utils.Logs;
using Microsoft.Win32;

namespace ChangJing.App.Services;

/// <summary>读取资源管理器显示隐藏文件 / 系统文件的设置</summary>
// 数据源为注册表 HKCU\...\Explorer\Advanced 的 Hidden 与 ShowSuperHidden，读取失败按不显示处理
public static class ExplorerSettings
{
    private const string AdvancedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    private const int HiddenShowValue = 1;

    /// <summary>是否显示隐藏文件，即资源管理器「隐藏的项目」已勾选</summary>
    public static bool ShowHiddenFiles => ReadInt("Hidden") == HiddenShowValue;

    /// <summary>是否显示系统文件，即「隐藏受保护的操作系统文件」未勾选</summary>
    public static bool ShowSystemFiles => ReadInt("ShowSuperHidden") == 1;

    /// <summary>是否需要把隐藏或系统条目列入文件列表</summary>
    public static bool ShouldShowEntry(bool isHidden, bool isSystem)
    {
        if (isSystem && !ShowSystemFiles)
        {
            return false;
        }

        if (isHidden && !ShowHiddenFiles)
        {
            return false;
        }

        return true;
    }

    private static int ReadInt(string valueName)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(AdvancedKeyPath, writable: false);
            if (key is null)
            {
                return -1;
            }

            object? value = key.GetValue(valueName);
            return value is int intValue ? intValue : -1;
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(ExplorerSettings), $"读取资源管理器设置 {valueName} 失败：{ex.Message}");
            return -1;
        }
    }
}
