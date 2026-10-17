using System.Windows;
using ChangJing.App.Core.Settings;
using ChangJing.App.ViewModels;
using ChangJing.App.Views;

namespace ChangJing.Regression;

/// <summary>公开发布前的界面回归：工具栏「设置」/「修复路径」按钮可见性 + 「关于」页信息</summary>
public static class PublishSuite
{
    private const string ExpectedVersion = "1.0.0";

    /// <summary>在已创建的窗口上运行；dataDir 为可导航的普通测试目录</summary>
    public static void Run(MainWindow window, AppSettings settings, string dataDir)
    {
        H.Group("C1 工具栏按钮可见性 + 关于页");

        MainViewModel vm = window.ViewModel;

        var settingsButton = window.FindName("SettingsButton") as UIElement;
        var repairButton = window.FindName("RepairPathsButton") as UIElement;
        H.Check("工具栏存在「设置」按钮", settingsButton is not null, "未找到 SettingsButton");
        H.Check("工具栏存在「修复路径…」按钮", repairButton is not null, "未找到 RepairPathsButton");
        H.Check("工具栏不再有「⋯」更多按钮", window.FindName("MoreDropDownButton") is null);

        // 此电脑：设置可见、修复路径不可见
        Visit(vm, MainViewModel.ThisPcToken);
        H.Check("此电脑页：设置按钮可见", IsVisible(settingsButton));
        H.Check("此电脑页：修复路径按钮隐藏", !IsVisible(repairButton));

        // 普通文件夹：两者都可见
        Visit(vm, dataDir);
        H.Check("文件夹页：设置按钮可见", IsVisible(settingsButton));
        H.Check("文件夹页：修复路径按钮可见", IsVisible(repairButton));

        // 搜索结果：设置可见、修复路径可见（原来可用的页面）
        vm.SearchText = "alpha";
        if (vm.SearchInSubfoldersCommand.CanExecute(null))
        {
            vm.SearchInSubfoldersCommand.Execute(null);
        }

        Program.PumpUntil(() => vm.IsSearchResultView, 8000);
        Program.Pump(150);
        H.Check("搜索页：进入搜索结果视图", vm.IsSearchResultView);
        H.Check("搜索页：设置按钮可见", IsVisible(settingsButton));
        H.Check("搜索页：修复路径按钮可见", IsVisible(repairButton));
        vm.CancelRecursiveSearch(clearResults: true);
        vm.SearchText = string.Empty;
        Program.Pump(150);

        // 回收站：设置可见、修复路径不可见
        Visit(vm, MainViewModel.RecycleBinToken);
        H.Check("回收站页：设置按钮可见", IsVisible(settingsButton));
        H.Check("回收站页：修复路径按钮隐藏", !IsVisible(repairButton));

        AboutPageChecks(settings);
    }

    private static void AboutPageChecks(AppSettings settings)
    {
        var about = new AboutViewModel();

        H.Eq("关于：应用名", "畅径", about.AppName);
        H.Eq("关于：版本", ExpectedVersion, about.Version);
        H.Check("关于：版本行含版本号与位数",
            about.VersionText.Contains(ExpectedVersion, StringComparison.Ordinal)
            && (about.VersionText.Contains("64 位", StringComparison.Ordinal)
                || about.VersionText.Contains("32 位", StringComparison.Ordinal)),
            about.VersionText);
        H.Check("关于：一句话说明正确",
            about.Tagline == "处理资源管理器打不开、删不掉的长路径文件", about.Tagline);
        H.Check("关于：隐私说明正确",
            about.PrivacyText == "完全本地运行，不联网，不收集任何数据", about.PrivacyText);
        H.Check("关于：版权行含作者与 GitHub 名",
            about.CopyrightLine.Contains("鱼塘泛舟-SQ", StringComparison.Ordinal)
            && about.CopyrightLine.Contains("GitHubMacrro", StringComparison.Ordinal),
            about.CopyrightLine);
        H.Check("关于：许可证行写明 MIT", about.LicenseLine.Contains("MIT", StringComparison.Ordinal), about.LicenseLine);
        H.Check("关于：反馈问题指向 issues",
            AboutViewModel.IssuesUrl.EndsWith("/issues", StringComparison.Ordinal), AboutViewModel.IssuesUrl);
        H.Check("关于：项目主页与 issues 同仓库",
            AboutViewModel.IssuesUrl.StartsWith(AboutViewModel.HomepageUrl, StringComparison.Ordinal));

        string info = about.BuildVersionInfo();
        H.Check("关于：复制版本信息含版本号", info.Contains(ExpectedVersion, StringComparison.Ordinal), info);
        H.Check("关于：复制版本信息含位数与 Windows 版本号",
            (info.Contains("64 位", StringComparison.Ordinal) || info.Contains("32 位", StringComparison.Ordinal))
            && info.Contains("Windows", StringComparison.Ordinal),
            info);

        // 设置窗口结构：选项 + 关于两个分页
        try
        {
            var dialog = new SettingsDialog(new SettingsViewModel(settings));
            H.Check("选项窗口有「选项」分页", dialog.HasOptionsTab);
            H.Check("选项窗口有「关于」分页", dialog.HasAboutTab);
        }
        catch (Exception ex)
        {
            H.Check("选项窗口可创建并含关于分页", false, ex.Message);
        }
    }

    private static void Visit(MainViewModel vm, string location)
    {
        vm.NavigateToLocation(location);
        Program.PumpUntil(() => !vm.IsLoading, 10000);
        Program.Pump(120);
    }

    private static bool IsVisible(UIElement? element)
        => element is not null && element.Visibility == Visibility.Visible;
}
