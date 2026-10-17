using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using ChangJing.App.Controls;
using ChangJing.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CustomWin.Utils.Logs;

namespace ChangJing.App.ViewModels;

/// <summary>「关于」分页的视图模型：版本信息、许可与帮助入口</summary>
// 所有联网动作只在用户点击时发生（用默认浏览器打开链接），程序自身不发起任何请求
public sealed partial class AboutViewModel : ObservableObject
{
    /// <summary>项目主页</summary>
    public const string HomepageUrl = "https://github.com/GitHubMacrro/changjing-file-manager";

    /// <summary>问题反馈页</summary>
    public const string IssuesUrl = "https://github.com/GitHubMacrro/changjing-file-manager/issues";

    /// <summary>GitHub 上的 MIT 许可证原文（安装目录找不到许可文件时的回退）</summary>
    public const string LicenseUrl = "https://github.com/GitHubMacrro/changjing-file-manager/blob/main/LICENSE";

    public AboutViewModel()
    {
        Version = ResolveVersion();
        CopyrightLine = "Copyright (c) 2026 鱼塘泛舟-SQ (GitHubMacrro)";
    }

    /// <summary>应用名</summary>
    public string AppName => "畅径";

    /// <summary>形如「版本 1.0.0（64 位）」；32 位进程显示「32 位」</summary>
    public string VersionText => $"版本 {Version}（{(Environment.Is64BitProcess ? "64 位" : "32 位")}）";

    /// <summary>一句话说明</summary>
    public string Tagline => "处理资源管理器打不开、删不掉的长路径文件";

    /// <summary>隐私说明</summary>
    public string PrivacyText => "完全本地运行，不联网，不收集任何数据";

    /// <summary>版权行</summary>
    public string CopyrightLine { get; }

    /// <summary>许可证说明</summary>
    public string LicenseLine => "以 MIT 许可证开源";

    /// <summary>应用图标（标题栏同源，按 2.0 缩放取较大图层）</summary>
    public ImageSource? AppIcon => AppIconProvider.GetTitleBarIcon(2.0);

    /// <summary>复制版本信息后的状态提示</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>解析出的程序版本号，取不到时回退 1.0.0</summary>
    public string Version { get; }

    [RelayCommand]
    private void OpenHomepage() => OpenUrl(HomepageUrl, "项目主页");

    [RelayCommand]
    private void ReportIssue() => OpenUrl(IssuesUrl, "反馈问题");

    /// <summary>查看许可证：优先安装目录的 LICENSE.txt / LICENSE / licenses，找不到打开 GitHub</summary>
    [RelayCommand]
    private void ViewLicense()
    {
        string? local = ResolveLocalLicense();
        if (local is not null)
        {
            OpenPath(local, "许可证");
            return;
        }

        OpenUrl(LicenseUrl, "许可证");
    }

    [RelayCommand]
    private void OpenLogsFolder() => OpenPath(AppPaths.LogsDirectory, "日志文件夹");

    /// <summary>复制版本、位数、Windows 版本号到剪贴板</summary>
    [RelayCommand]
    private void CopyVersionInfo()
    {
        try
        {
            Clipboard.SetText(BuildVersionInfo());
            StatusMessage = "已复制";
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(AboutViewModel), $"复制版本信息失败：{ex.Message}");
            StatusMessage = "复制失败";
        }
    }

    /// <summary>版本信息文本：版本、位数、Windows 版本号</summary>
    public string BuildVersionInfo()
        => $"{AppName} {Version}（{(Environment.Is64BitProcess ? "64 位" : "32 位")}）{Environment.NewLine}" +
           $"Windows {Environment.OSVersion.Version}";

    private static string ResolveVersion()
    {
        try
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            string? informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                // SDK 会在 InformationalVersion 追加「+<commit 哈希>」构建元数据，面向用户的版本号去掉它
                int plus = informational!.IndexOf('+');
                string version = plus >= 0 ? informational.Substring(0, plus) : informational;
                if (version.Length > 0)
                {
                    return version;
                }
            }

            return assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        }
        catch
        {
            return "1.0.0";
        }
    }

    /// <summary>在安装目录里找许可文件：LICENSE.txt / LICENSE / LICENSE.md / licenses 目录</summary>
    private static string? ResolveLocalLicense()
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            foreach (string name in new[] { "LICENSE.txt", "LICENSE", "LICENSE.md" })
            {
                string path = Path.Combine(baseDir, name);
                if (File.Exists(path))
                {
                    return path;
                }
            }

            string licensesDir = Path.Combine(baseDir, "licenses");
            if (Directory.Exists(licensesDir))
            {
                return licensesDir;
            }
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(AboutViewModel), $"定位本地许可文件失败：{ex.Message}");
        }

        return null;
    }

    // 用默认程序打开路径 / 用默认浏览器打开链接，仅用户点击时触发
    private void OpenPath(string path, string what)
        => StartProcess(new ProcessStartInfo { FileName = path, UseShellExecute = true }, what);

    private void OpenUrl(string url, string what)
    {
        try
        {
            StartProcess(new ProcessStartInfo { FileName = url, UseShellExecute = true }, what);
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(AboutViewModel), $"打开{what}失败：{ex.Message}");
            StatusMessage = $"无法打开{what}";
        }
    }

    private void StartProcess(ProcessStartInfo info, string what)
    {
        try
        {
            Process.Start(info);
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(AboutViewModel), $"打开{what}失败：{ex.Message}");
            StatusMessage = $"无法打开{what}";
        }
    }
}
