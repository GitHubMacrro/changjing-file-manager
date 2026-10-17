using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using ChangJing.App.Core.FileSystem;
using ChangJing.App.Core.LongPath;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Services;

/// <summary>「打开方式」列表里的一项</summary>
public sealed class OpenWithHandler
{
    public OpenWithHandler(string displayName, string command)
    {
        DisplayName = displayName;
        Command = command;
    }

    public string DisplayName { get; }

    /// <summary>可执行文件路径</summary>
    public string Command { get; }
}

/// <summary>「打开方式」服务：列出系统推荐的关联程序，并用指定程序打开文件</summary>
// 任何一步失败都降级，不向界面抛异常
public static class OpenWithService
{
    /// <summary>推荐程序最多列出几项</summary>
    public const int MaxHandlers = 6;

    /// <summary>枚举该文件扩展名注册的推荐处理程序</summary>
    public static IReadOnlyList<OpenWithHandler> EnumerateRecommended(string displayPath)
    {
        var result = new List<OpenWithHandler>(MaxHandlers);

        string extension = PathHelper.GetExtension(PathHelper.GetName(displayPath));
        if (extension.Length == 0)
        {
            // 无扩展名：系统不会给出有意义的关联列表，只留「选择其他应用」
            return result;
        }

        OpenWithNativeMethods.IEnumAssocHandlers? enumerator = null;
        try
        {
            int hr = OpenWithNativeMethods.SHAssocEnumHandlers(
                extension,
                OpenWithNativeMethods.AssocFilterRecommended,
                out enumerator);

            if (hr != 0 || enumerator is null)
            {
                LogManager.Debug(typeof(OpenWithService), $"枚举打开方式失败（{extension}，HRESULT {hr:X8}）。");
                return result;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (result.Count < MaxHandlers)
            {
                OpenWithNativeMethods.IAssocHandler? handler = null;
                try
                {
                    hr = enumerator.Next(1, out handler, out uint fetched);
                    if (hr != 0 || fetched == 0 || handler is null)
                    {
                        break;
                    }

                    string command = ReadName(handler, uiName: false);
                    string uiName = ReadName(handler, uiName: true);

                    if (command.Length == 0)
                    {
                        continue;
                    }

                    // 同一可执行文件的多个关联只留一个
                    if (!seen.Add(command))
                    {
                        continue;
                    }

                    string display = uiName.Length > 0
                        ? uiName
                        : Path.GetFileNameWithoutExtension(command);
                    if (display.Length == 0)
                    {
                        display = command;
                    }

                    result.Add(new OpenWithHandler(display, command));
                }
                finally
                {
                    ReleaseComObject(handler);
                }
            }
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(OpenWithService), $"枚举打开方式异常：{ex.Message}");
        }
        finally
        {
            ReleaseComObject(enumerator);
        }

        return result;
    }

    /// <summary>用指定程序打开文件；路径过长 / 名称特殊时先走短根联接或临时副本</summary>
    public static bool TryOpenWith(string displayPath, string executable, out string error)
    {
        error = string.Empty;

        if (!TryResolveOpenablePath(displayPath, out string target, out string prepareError))
        {
            error = prepareError;
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true,
            };
            startInfo.ArgumentList.Add(target);

            Process.Start(startInfo);
            LogManager.Info(typeof(OpenWithService), $"已用指定程序打开：{target} → {executable}");
            return true;
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(OpenWithService), $"用指定程序打开失败（{executable}）：{ex.Message}");
            error = $"无法用该程序打开：{ex.Message}";
            return false;
        }
    }

    /// <summary>弹出系统「打开方式」对话框</summary>
    public static bool TryOpenAsDialog(string displayPath, out string error)
    {
        error = string.Empty;

        if (!TryResolveOpenablePath(displayPath, out string target, out string prepareError))
        {
            error = prepareError;
            return false;
        }

        if (!ShellOpener.TryOpenVerb(target, "openas", out ShellOpenResult result))
        {
            error = result.Error;
            return false;
        }

        LogManager.Info(typeof(OpenWithService), $"已请求系统「选择其他应用」：{target}");
        return true;
    }

    private static bool TryResolveOpenablePath(string displayPath, out string target, out string error)
    {
        target = displayPath;
        error = string.Empty;

        string name = PathHelper.GetName(PathHelper.ToDisplay(displayPath));
        bool specialName = LongPathRules.HasTrailingDotOrSpace(name) || LongPathRules.IsReservedName(name);

        if (displayPath.Length <= LongPathRules.MaxExplorerPathLength && !specialName)
        {
            return true;
        }

        if (!specialName
            && OpenShortRoot.TryPrepareFile(displayPath, out string? shortPath)
            && !string.IsNullOrEmpty(shortPath))
        {
            target = shortPath!;
            return true;
        }

        if (TempOpenCopy.TryCopy(displayPath, out string? copied, out string copyError)
            && !string.IsNullOrEmpty(copied))
        {
            target = copied!;
            return true;
        }

        error = $"无法为该文件准备可打开的路径：{copyError}";
        LogManager.Warning(typeof(OpenWithService), error);
        return false;
    }

    private static string ReadName(OpenWithNativeMethods.IAssocHandler handler, bool uiName)
    {
        try
        {
            string value;
            int hr = uiName ? handler.GetUIName(out value) : handler.GetName(out value);
            return hr == 0 && value is not null ? value : string.Empty;
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(OpenWithService), $"读取关联程序信息失败：{ex.Message}");
            return string.Empty;
        }
    }

    private static void ReleaseComObject(object? instance)
    {
        if (instance is null || !Marshal.IsComObject(instance))
        {
            return;
        }

        try
        {
            Marshal.FinalReleaseComObject(instance);
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(OpenWithService), $"释放 COM 对象失败：{ex.Message}");
        }
    }
}
