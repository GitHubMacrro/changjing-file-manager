using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Services;

/// <summary>提权支持：runas 启动提权进程，用临时 JSON 文件传文件夹路径</summary>
// 用临时 JSON 文件传路径，命令行有长度与转义限制
public static class ElevationService
{
    /// <summary>命令行参数名：其后跟临时 JSON 文件的路径</summary>
    public const string FolderArgumentName = "--folder";

    // SW_SHOWNORMAL
    private const int SwShownormal = 1;

    // ShellExecute 返回值大于该值才算成功，≤ 32 是 SE_ERR_* 错误码
    private const int ShellExecuteSuccessThreshold = 32;

    private const string ElevationDirectoryName = "elevation";

    private static bool? _isElevated;

    /// <summary>测试用覆盖值，非 null 时优先于真实判断</summary>
    public static bool? IsElevatedOverride { get; set; }

    /// <summary>当前进程是否管理员运行，结果缓存，异常按未提权处理</summary>
    public static bool IsElevated
    {
        get
        {
            if (IsElevatedOverride is { } overridden)
            {
                return overridden;
            }

            _isElevated ??= DetectElevated();
            return _isElevated.Value;
        }
    }

    /// <summary>以管理员身份重启当前程序，并把 folderDisplayPath 传给新进程</summary>
    public static bool TryRelaunchElevated(string? folderDisplayPath, out string error)
    {
        error = string.Empty;

        string exePath;
        try
        {
            exePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(ElevationService), ex);
            error = "无法确定当前程序的位置，提权启动失败。";
            return false;
        }

        if (string.IsNullOrEmpty(exePath))
        {
            error = "无法确定当前程序的位置，提权启动失败。";
            return false;
        }

        string jsonPath;
        try
        {
            jsonPath = WriteHandoffFile(folderDisplayPath);
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(ElevationService), ex);
            error = $"准备提权参数失败：{ex.Message}";
            return false;
        }

        string parameters = $"{FolderArgumentName} \"{jsonPath}\"";

        try
        {
            IntPtr result = ShellExecuteW(
                IntPtr.Zero,
                "runas",
                exePath,
                parameters,
                null,
                SwShownormal);

            long code = result.ToInt64();
            if (code > ShellExecuteSuccessThreshold)
            {
                LogManager.Info(typeof(ElevationService), $"已发起提权启动：{exePath} {parameters}");
                return true;
            }

            // 1223 = ERROR_CANCELLED，用户拒绝 UAC，ShellExecute 也常返回 5
            int lastError = Marshal.GetLastWin32Error();
            if (code == 5 || lastError == 1223)
            {
                error = "已取消提权（未授予管理员权限）。";
            }
            else
            {
                error = $"以管理员身份启动失败：{DescribeShellError((int)code)}";
            }

            LogManager.Warning(
                typeof(ElevationService),
                $"提权启动失败：代码 {code}，Win32 错误 {lastError}，程序 {exePath}");
            return false;
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(ElevationService), ex);
            error = $"以管理员身份启动时发生错误：{ex.Message}";
            return false;
        }
    }

    /// <summary>新进程启动早期调用，若命令行含 --folder &lt;jsonfile&gt; 则读取并删除临时文件</summary>
    public static string? ConsumeStartupFolder(string[] args)
    {
        if (args is null || args.Length == 0)
        {
            return null;
        }

        string? jsonPath = FindFolderArgument(args);
        if (string.IsNullOrEmpty(jsonPath))
        {
            return null;
        }

        try
        {
            if (!File.Exists(jsonPath))
            {
                LogManager.Warning(typeof(ElevationService), $"提权交接文件不存在：{jsonPath}");
                return null;
            }

            string json = File.ReadAllText(jsonPath, Encoding.UTF8);
            HandoffPayload? payload = JsonSerializer.Deserialize<HandoffPayload>(json);

            TryDeleteFile(jsonPath);

            string? folder = payload?.Folder;
            if (string.IsNullOrEmpty(folder))
            {
                LogManager.Warning(typeof(ElevationService), $"提权交接文件内容为空：{jsonPath}");
                return null;
            }

            LogManager.Info(typeof(ElevationService), $"已读取提权交接的文件夹：{folder}");
            return folder;
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(ElevationService), $"读取提权交接文件失败：{jsonPath} — {ex.Message}");
            TryDeleteFile(jsonPath);
            return null;
        }
    }

    /// <summary>写入一次提权交接用的临时 JSON 文件，返回其完整路径</summary>
    public static string WriteHandoffFile(string? folderDisplayPath)
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChangJing",
            ElevationDirectoryName);

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            throw new IOException($"无法创建提权临时目录：{ex.Message}", ex);
        }

        string path = Path.Combine(directory, $"elevation-{Guid.NewGuid():N}.json");
        string json = JsonSerializer.Serialize(new HandoffPayload { Folder = folderDisplayPath });

        try
        {
            File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            throw new IOException($"无法写入提权交接文件：{ex.Message}", ex);
        }

        return path;
    }

    private static string? FindFolderArgument(string[] args)
    {
        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index] ?? string.Empty;

            if (string.Equals(arg, FolderArgumentName, StringComparison.OrdinalIgnoreCase))
            {
                return index + 1 < args.Length ? args[index + 1] : null;
            }

            string prefix = FolderArgumentName + "=";
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string value = arg.Substring(prefix.Length);
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            // 删不掉不影响功能，残留由后续清理兜底
            LogManager.Warning(typeof(ElevationService), $"删除提权交接文件失败：{path} — {ex.Message}");
        }
    }

    private static bool DetectElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(ElevationService), $"判断提权状态失败：{ex.Message}");
            return false;
        }
    }

    private static string DescribeShellError(int code)
    {
        return code switch
        {
            0 => "系统资源不足。",
            2 => "找不到指定的文件。",
            3 => "找不到指定的路径。",
            5 => "访问被拒绝。",
            8 => "内存不足。",
            26 => "文件共享冲突。",
            27 => "文件关联不完整。",
            28 => "操作超时。",
            29 => "DDE 事务失败。",
            30 => "DDE 事务忙。",
            31 => "没有可用的关联程序。",
            32 => "无法加载所需的动态链接库。",
            _ => $"未知错误（代码 {code}）。",
        };
    }

    /// <summary>ShellExecuteW：runas 动词请求提权</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr ShellExecuteW(
        IntPtr hwnd,
        string lpOperation,
        string lpFile,
        string? lpParameters,
        string? lpDirectory,
        int nShowCmd);

    internal sealed class HandoffPayload
    {
        /// <summary>要操作的文件夹普通路径</summary>
        public string? Folder { get; set; }
    }
}
