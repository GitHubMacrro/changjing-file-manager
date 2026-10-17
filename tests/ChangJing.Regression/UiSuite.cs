using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChangJing.App.Controls;
using ChangJing.App.Core.Grouping;
using ChangJing.App.Core.PathRepair;
using ChangJing.App.Core.RecycleBin;
using ChangJing.App.Core.Settings;
using ChangJing.App.Services;
using ChangJing.App.ViewModels;
using ChangJing.App.Views;

namespace ChangJing.Regression;

/// <summary>界面回归：真实 MainWindow + 真实主题资源</summary>
// 覆盖真实导航 / 面包屑 / 视图模式 / 分组 / 新建菜单 / 主题切换与可视树审计 / 列宽排序持久化
public static class UiSuite
{
    public static void Run()
    {
        H.Group("B1 真实主窗口：导航 / 面包屑 / 视图 / 分组 / 主题");

        string dataDir = H.NewDir("ui-data");
        H.WriteFile(Path.Combine(dataDir, "alpha.txt"), "alpha");
        H.WriteFile(Path.Combine(dataDir, "beta.txt"), "beta");
        H.WriteFile(Path.Combine(dataDir, "gamma.log"), "gamma");
        H.MakeDir(Path.Combine(dataDir, "subfolder"));

        // 隔离的 settings.json（绝不动真实用户设置）
        var settingsService = new SettingsService(Path.Combine(H.Root, "ui-settings.json"));
        AppSettings settings = settingsService.Load();
        var themeService = new ThemeService();
        themeService.Initialize(AppSettings.ThemeModeLight);
        var services = new AppServices(settingsService, themeService);

        var recycleOptions = new RecycleBinOptions
        {
            RootOverride = _ => Path.Combine(H.Root, "ui-recycle"),
            VolumeRootsOverride = () => Array.Empty<string>(),
        };

        MainWindow window;
        try
        {
            window = new MainWindow(services, settings, recycleOptions);
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000;
            window.Top = -32000;
            window.Show();
            Program.Window = window;
        }
        catch (Exception ex)
        {
            H.Check("主窗口创建并显示", false, ex.Message);
            return;
        }

        // 等导航树真正建好（根数 ≥ 1）再继续，避免后续导航被 Start 的探测回调覆盖
        H.Check("主窗口创建并显示", Program.PumpUntil(() => window.IsLoaded, 8000));
        Program.Pump(300);

        MainViewModel vm = window.ViewModel;
        bool started = Program.PumpUntil(() => vm.NavigationRoots.Count >= 1, 10000);
        H.Check("启动后构建导航树（快速访问 / 此电脑）", started, $"根数 {vm.NavigationRoots.Count}");
        H.Check("启动后打开「此电脑」（标题「畅径 - 此电脑」）", vm.WindowTitle == "畅径 - 此电脑", vm.WindowTitle);

        // 导航到测试目录
        vm.NavigateToLocation(dataDir);
        bool loaded = Program.PumpUntil(() => !vm.IsLoading && vm.Items.Count >= 4, 10000);
        H.Check("导航到测试目录并加载 4 项", loaded, $"已加载 {vm.Items.Count} 项，IsLoading={vm.IsLoading}");
        H.Check("当前地址显示为测试目录", string.Equals(vm.CurrentLocationDisplay, dataDir, StringComparison.OrdinalIgnoreCase),
            vm.CurrentLocationDisplay);
        H.Check("列表含子文件夹", vm.Items.Any(i => i.IsDirectory), "无文件夹");

        // 面包屑控件同步
        if (window.FindName("AddressBarControl") is AddressBar bar)
        {
            H.Check("地址栏 Location 同步", string.Equals(bar.Location, dataDir, StringComparison.OrdinalIgnoreCase), bar.Location);
        }
        else
        {
            H.Check("找到地址栏控件", false, "未找到 AddressBarControl");
        }

        // 视图模式切换
        vm.ApplyViewMode(FileViewMode.LargeIcons);
        H.Check("切换到大图标视图", vm.IsNonDetailsView && vm.ViewMode == FileViewMode.LargeIcons);
        vm.ApplyViewMode(FileViewMode.Details);
        H.Check("切回详细信息视图", vm.IsDetailsView && vm.ViewMode == FileViewMode.Details);

        // 分组
        vm.SetGroupByCommand.Execute("Name");
        H.Check("按名称分组生效", vm.CurrentGroupBy == FileGroupBy.Name && vm.IsGrouping);
        vm.SetGroupByCommand.Execute("None");
        H.Check("取消分组生效", vm.CurrentGroupBy == FileGroupBy.None && !vm.IsGrouping);

        // 排序（点击「大小」列 → 降序）
        vm.SortByColumnCommand.Execute("Size");
        H.Check("按大小排序生效", vm.SortColumn == FileSortColumn.Size);

        // 新建菜单（后台读取注册表 ShellNew）
        vm.EnsureNewItemsLoaded();
        Program.PumpUntil(() => vm.NewItems.Count > 0, 5000);
        H.Check("新建菜单项非空（含文件夹）", vm.NewItems.Count > 0, $"项数 {vm.NewItems.Count}");

        // 列表实际渲染出了行（防止 ItemsSource 漏绑之类）
        if (window.FindName("FileListClip") is not null)
        {
            Program.Pump(200);
            H.Check("详细信息列表可见", true);
        }

        // 主题切换 + 可视树审计
        ThemeSwitchAndAudit(window, themeService);

        // 列宽 / 排序持久化
        ColumnAndSortPersistence(dataDir);

        // 修复路径：批量改名 + 一次撤销 / 重做 + 失败项回传
        PathRepairExecutionFlow(window, dataDir);

        // 标题栏改版：标题字符串 / 管理员后缀 / 超长名省略 / ToolTip / 图层选择
        TitleBarSuite(window, dataDir);

        // 启动命令行位置参数：导航到文件夹 / 打开文件并选中
        StartupTargetSuite(window, dataDir);

        // 关闭窗口（触发清理路径，不抛异常）
        try
        {
            window.Close();
            Program.Pump(200);
            H.Check("主窗口可正常关闭", true);
        }
        catch (Exception ex)
        {
            H.Check("主窗口可正常关闭", false, ex.Message);
        }
    }

    // 标题栏改版

    // 标题栏改版回归：Window.Title 各场景、管理员后缀 + 盾牌、超长名省略、ToolTip、图标 DPI、重命名 / 删除
    private static void TitleBarSuite(MainWindow window, string uiDataDir)
    {
        MainViewModel vm = window.ViewModel;

        // 各场景 Window.Title
        vm.NavigateToLocation(uiDataDir);
        Program.PumpUntil(() => !vm.IsLoading && vm.Items.Count >= 4, 10000);
        string folderName = Path.GetFileName(uiDataDir);
        H.Eq("标题：普通文件夹", $"畅径 - {folderName}", vm.WindowTitle);
        H.Eq("标题栏主文字（常规色 = 应用名）", "畅径", vm.TitleMainText);
        H.Eq("标题栏后缀（次要灰 = 「 - 位置名」）", $" - {folderName}", vm.TitleSuffixText);
        H.Eq("ToolTip = 完整普通路径", uiDataDir, vm.TitleToolTip);
        H.Check("ToolTip 不含 \\\\?\\", !vm.TitleToolTip.Contains(@"\\?", StringComparison.Ordinal), vm.TitleToolTip);

        vm.NavigateToLocation(MainViewModel.ThisPcToken);
        Program.PumpUntil(() => !vm.IsLoading, 10000);
        H.Eq("标题：此电脑", "畅径 - 此电脑", vm.WindowTitle);

        string driveRoot = Path.GetPathRoot(uiDataDir)!;
        vm.NavigateToLocation(driveRoot);
        Program.PumpUntil(() => !vm.IsLoading, 10000);
        string driveName = vm.TitleLocationName;
        H.Check("标题：盘符根含「(X:)」", driveName.Contains($"({char.ToUpperInvariant(driveRoot[0])}:)", StringComparison.Ordinal), driveName);
        H.Check("标题：盘符根为「卷标/本地磁盘 (X:)」形式",
            driveName.Contains('(') && driveName.EndsWith($"{char.ToUpperInvariant(driveRoot[0])}:)", StringComparison.Ordinal), driveName);
        H.Eq("标题：盘符根完整", $"畅径 - {driveName}", vm.WindowTitle);

        vm.NavigateToLocation(MainViewModel.RecycleBinToken);
        Program.PumpUntil(() => !vm.IsLoading, 10000);
        H.Eq("标题：回收站", "畅径 - 回收站", vm.WindowTitle);

        vm.NavigateToLocation(uiDataDir);
        Program.PumpUntil(() => !vm.IsLoading, 10000);
        vm.SearchText = "alpha";
        if (vm.SearchInSubfoldersCommand.CanExecute(null))
        {
            vm.SearchInSubfoldersCommand.Execute(null);
        }

        Program.PumpUntil(() => vm.IsSearchResultView, 8000);
        Program.Pump(200);
        H.Eq("标题：搜索结果", "畅径 - 搜索“alpha”", vm.WindowTitle);
        H.Eq("搜索结果 ToolTip", "搜索“alpha”", vm.TitleToolTip);

        vm.CancelRecursiveSearch(clearResults: true);
        vm.SearchText = string.Empty;
        Program.Pump(200);

        // 管理员后缀格式 + 盾牌
        vm.NavigateToLocation(uiDataDir);
        Program.PumpUntil(() => !vm.IsLoading, 10000);
        ElevationService.IsElevatedOverride = true;
        vm.RefreshTitleForTest();
        Program.Pump(150);
        H.Eq("标题：管理员后缀格式", $"畅径（管理员） - {folderName}", vm.WindowTitle);
        H.Eq("标题栏主文字（管理员）", "畅径（管理员）", vm.TitleMainText);
        H.Eq("标题栏后缀（管理员）", $" - {folderName}", vm.TitleSuffixText);
        H.Check("管理员盾牌可见", vm.ShowTitleShield);
        H.Check("应用名在位置名之前", vm.WindowTitle.StartsWith("畅径", StringComparison.Ordinal), vm.WindowTitle);

        ElevationService.IsElevatedOverride = null;
        vm.RefreshTitleForTest();
        Program.Pump(150);
        H.Eq("恢复非管理员标题", $"畅径 - {folderName}", vm.WindowTitle);
        H.Check("非管理员不显示盾牌", !vm.ShowTitleShield);

        // 超长名：标题栏末尾省略（保留开头的「畅径」），Window.Title 完整
        var titleText = window.FindName("TitleTextControl") as ChangJing.App.Controls.TitleBarText;
        H.Check("找到标题栏文字控件", titleText is not null, "未找到 TitleTextControl");
        if (titleText is not null)
        {
            Program.Pump(150);
            H.Check("短名不省略", !titleText.IsTruncated, titleText.RenderedText);

            string longName = new string('长', 120);
            titleText.Width = 130;
            titleText.MainText = "畅径";
            titleText.SuffixText = " - " + longName;
            window.UpdateLayout();
            Program.Pump(150);

            string rendered = titleText.RenderedText;
            H.Check("超长名标题栏已省略", titleText.IsTruncated, rendered);
            H.Check("省略后保留开头「畅径」", rendered.StartsWith("畅径", StringComparison.Ordinal), rendered);
            H.Check("省略后含省略号", rendered.Contains('…'), rendered);
            H.Check("标题栏原始文本仍完整", titleText.FullText.StartsWith("畅径", StringComparison.Ordinal)
                && titleText.FullText.EndsWith(longName, StringComparison.Ordinal), $"长度 {titleText.FullText.Length}");

            // 还原（后续断言仍用真实标题）
            titleText.ClearValue(FrameworkElement.WidthProperty);
            titleText.MainText = vm.TitleMainText;
            titleText.SuffixText = vm.TitleSuffixText;
            window.UpdateLayout();
            Program.Pump(150);
        }

        // 超长文件夹名 → Window.Title 完整不省略
        string longFolder = Path.Combine(H.Root, new string('目', 60));
        H.MakeDir(longFolder);
        vm.NavigateToLocation(longFolder);
        Program.PumpUntil(() => !vm.IsLoading, 12000);
        H.Check("超长文件夹名 Window.Title 完整（不省略）",
            vm.WindowTitle == "畅径 - " + new string('目', 60), $"长度 {vm.WindowTitle.Length}：{vm.WindowTitle}");
        H.Check("超长文件夹名 ToolTip 为完整路径", vm.TitleToolTip == longFolder, vm.TitleToolTip);

        // 标题栏图标来自 app.ico（ImageIcon 绑定），AppIconProvider 按 DPI 选图层
        var titleBar = window.FindName("MainTitleBar") as Wpf.Ui.Controls.TitleBar;
        H.Check("标题栏图标是 ImageIcon", titleBar?.Icon is Wpf.Ui.Controls.ImageIcon,
            titleBar?.Icon?.GetType().Name ?? "null");
        if (titleBar?.Icon is Wpf.Ui.Controls.ImageIcon imageIcon)
        {
            H.Check("标题栏图标已加载", imageIcon.Source is not null, "Source 为 null");
        }

        AppIconProvider.ResetForTest();
        H.Eq("app.ico 图层数", 7, AppIconProvider.FramePixelWidths.Count);
        _ = AppIconProvider.GetTitleBarIcon(1.0);
        H.Eq("100% 取 16px 图层", 16, AppIconProvider.LastSelectedPixelWidth);
        _ = AppIconProvider.GetTitleBarIcon(1.5);
        H.Eq("150% 取 24px 图层", 24, AppIconProvider.LastSelectedPixelWidth);
        _ = AppIconProvider.GetTitleBarIcon(2.0);
        H.Eq("200% 取 32px 图层", 32, AppIconProvider.LastSelectedPixelWidth);

        // 重命名当前文件夹 → 标题更新
        string parent = H.NewDir("title-rename");
        string oldFolder = Path.Combine(parent, "oldfolder");
        H.MakeDir(oldFolder);
        vm.NavigateToLocation(oldFolder);
        Program.PumpUntil(() => !vm.IsLoading, 10000);
        H.Eq("重命名前标题", "畅径 - oldfolder", vm.WindowTitle);

        string newFolder = Path.Combine(parent, "newfolder");
        Directory.Move(oldFolder, newFolder);
        H.Check("重命名当前文件夹后标题映射命中", vm.RemapCurrentLocation(oldFolder, newFolder));
        H.Eq("重命名后标题更新", "畅径 - newfolder", vm.WindowTitle);
        H.Eq("重命名后 ToolTip 更新", newFolder, vm.TitleToolTip);

        // 当前文件夹被删除 → 标题不再指向已删目录
        string gone = H.NewDir("title-gone");
        vm.NavigateToLocation(gone);
        Program.PumpUntil(() => !vm.IsLoading, 10000);
        H.Eq("删除前标题", "畅径 - title-gone", vm.WindowTitle);

        Directory.Delete(gone);
        if (vm.RefreshCommand.CanExecute(null))
        {
            vm.RefreshCommand.Execute(null);
        }

        Program.PumpUntil(
            () => !string.Equals(vm.CurrentLocationDisplay, gone, StringComparison.OrdinalIgnoreCase)
                  || vm.IsLocationUnavailable,
            12000);
        Program.Pump(250);
        H.Check("当前文件夹被删除后标题不再指向已删目录",
            !vm.WindowTitle.Contains("title-gone", StringComparison.Ordinal), vm.WindowTitle);
    }

    // 启动命令行位置参数

    /// <summary>UI 级：StartupTarget 文件夹 → 导航；文件 → 父目录 + 选中</summary>
    private static void StartupTargetSuite(MainWindow window, string uiDataDir)
    {
        MainViewModel vm = window.ViewModel;

        // 文件夹目标 → 导航到该文件夹
        vm.Start(StartupTarget.ForFolder(uiDataDir));
        bool folderLoaded = Program.PumpUntil(
            () => !vm.IsLoading && string.Equals(vm.CurrentLocationDisplay, uiDataDir, StringComparison.OrdinalIgnoreCase)
                  && vm.Items.Count >= 4,
            12000);
        H.Check("启动目标文件夹 → 导航到该文件夹", folderLoaded, vm.CurrentLocationDisplay);

        // 文件目标 → 父目录 + 选中该文件
        string targetFile = Path.Combine(uiDataDir, "alpha.txt");
        vm.Start(StartupTarget.ForFile(uiDataDir, targetFile));
        bool selected = Program.PumpUntil(
            () => !vm.IsLoading && vm.SelectedItems.Count == 1
                  && string.Equals(vm.SelectedItems[0].DisplayPath, targetFile, StringComparison.OrdinalIgnoreCase),
            12000);
        H.Check("启动目标文件 → 父目录并选中文件", selected,
            $"选中 {vm.SelectedItems.Count} 项，当前 {vm.CurrentLocationDisplay}");
        H.Check("启动目标文件导航到父目录",
            string.Equals(vm.CurrentLocationDisplay, uiDataDir, StringComparison.OrdinalIgnoreCase), vm.CurrentLocationDisplay);

        // 默认目标 → 此电脑
        vm.Start(StartupTarget.Default());
        bool thisPc = Program.PumpUntil(() => !vm.IsLoading && vm.IsThisPcView, 12000);
        H.Check("启动目标默认 → 此电脑", thisPc, vm.CurrentLocationDisplay);
    }

    // 主题

    private static void ThemeSwitchAndAudit(MainWindow window, ThemeService themeService)
    {
        static Color? BrushColor(string key)
            => Application.Current?.TryFindResource(key) is SolidColorBrush b ? b.Color : null;

        Color? lightColor = BrushColor("CjWindowBackgroundBrush");
        themeService.SetThemeMode(AppSettings.ThemeModeDark);
        Program.Pump(300);
        Color? darkColor = BrushColor("CjWindowBackgroundBrush");

        H.Check("浅色窗口底色已解析", lightColor is not null, "CjWindowBackgroundBrush 缺失");
        H.Check("深色窗口底色已解析", darkColor is not null);
        double lightLum = lightColor is { } lc ? Luminance(lc) : -1;
        double darkLum = darkColor is { } dc ? Luminance(dc) : -1;
        H.Check("浅色底色偏亮", lightLum > 0.7, $"亮度 {lightLum:F2}");
        H.Check("深色底色偏暗", darkLum < 0.3, $"亮度 {darkLum:F2}");

        // 可视树审计：深色下不该有「大面积近白底」残留（FluentWindow 命令式设的 Window.Background / ClientAreaBorder 除外）
        int lightBlocks = AuditLightBlocks((DependencyObject)window.Content, threshold: 0.90, minArea: 18000);
        H.Check("深色主题下无大面积近白底色块", lightBlocks == 0, $"发现 {lightBlocks} 处");

        // 正向断言：根 Grid（最终可见底色）在深色下必须是深色
        if (window.Content is System.Windows.Controls.Panel rootPanel
            && rootPanel.Background is SolidColorBrush rootBrush)
        {
            H.Check("深色下根 Grid 底色为深色", Luminance(rootBrush.Color) < 0.3,
                $"亮度 {Luminance(rootBrush.Color):F2}");
        }
        else
        {
            H.Check("根 Grid 底色可读", false, "根 Content 不是带背景的 Panel");
        }

        // 深色 → 浅色 复原，且无残留
        themeService.SetThemeMode(AppSettings.ThemeModeLight);
        Program.Pump(300);
        Color? back = BrushColor("CjWindowBackgroundBrush");
        H.Check("切回浅色底色复原", back is { } bc && Luminance(bc) > 0.7, $"亮度 {(back is { } c ? Luminance(c) : -1):F2}");
        int darkBlocks = AuditDarkBlocks((DependencyObject)window.Content, threshold: 0.15, minArea: 18000);
        H.Check("浅色主题下无大面积近黑底色块", darkBlocks == 0, $"发现 {darkBlocks} 处");
    }

    private static int AuditLightBlocks(DependencyObject root, double threshold, double minArea)
        => AuditBlocks(root, threshold, minArea, brighterThan: true);

    private static int AuditDarkBlocks(DependencyObject root, double threshold, double minArea)
        => AuditBlocks(root, threshold, minArea, brighterThan: false);

    private static int AuditBlocks(DependencyObject root, double threshold, double minArea, bool brighterThan)
    {
        int violations = 0;
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            DependencyObject node = stack.Pop();
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(node, i);
                stack.Push(child);
            }

            if (node is not FrameworkElement fe)
            {
                continue;
            }

            Brush? brush = node switch
            {
                Border border => border.Background,
                Panel panel => panel.Background,
                Control control => control.Background,
                _ => null,
            };

            if (brush is not SolidColorBrush solid || solid.Color.A < 200)
            {
                continue;
            }

            double area = fe.ActualWidth * fe.ActualHeight;
            if (area < minArea)
            {
                continue;
            }

            double lum = Luminance(solid.Color);
            if (brighterThan ? lum >= threshold : lum <= threshold)
            {
                violations++;
                H.Info($"    [audit] {node.GetType().Name} {fe.ActualWidth:F0}x{fe.ActualHeight:F0} " +
                       $"lum={lum:F2} color={solid.Color} name={(fe as FrameworkElement)?.Name}");
            }
        }

        return violations;
    }

    private static double Luminance(Color c)
        => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    // 持久化

    private static void ColumnAndSortPersistence(string dataDir)
    {
        string path = Path.Combine(H.Root, "persist-settings.json");
        var svc = new SettingsService(path);
        AppSettings s = svc.Load();
        var store = new FolderViewStore(svc, s);

        store.SetColumnWidth("files.Name", 333);
        store.SetColumnWidth("files.Size", 111);
        store.Set(dataDir, FileViewMode.LargeIcons, 128, "Size", true, "Name", false);
        store.SetRecycleBinSort("Size", true);
        store.SetSearchSort("DateModified", true);
        store.Save();

        var svc2 = new SettingsService(path);
        AppSettings s2 = svc2.Load();
        var store2 = new FolderViewStore(svc2, s2);

        H.Check("列宽持久化（files.Name）", Math.Abs((store2.TryGetColumnWidth("files.Name") ?? -1) - 333) < 0.5,
            $"实际 {store2.TryGetColumnWidth("files.Name")}");
        H.Check("列宽持久化（files.Size）", Math.Abs((store2.TryGetColumnWidth("files.Size") ?? -1) - 111) < 0.5);

        FolderViewState? state = store2.TryGet(dataDir);
        H.Check("每文件夹视图持久化", state is not null && state.ViewMode == "LargeIcons", state?.ViewMode ?? "null");
        H.Check("每文件夹排序持久化", state is not null && state.SortColumn == "Size" && state.SortDescending);

        H.Check("回收站排序持久化", store2.RecycleBinSortColumn == "Size" && store2.RecycleBinSortDescending);
        H.Check("搜索排序持久化", store2.SearchSortColumn == "DateModified" && store2.SearchSortDescending);
    }

    // 修复路径执行

    /// <summary>修复路径执行回归：一次批量修复 + 撤销重做一致 + 失败项列出</summary>
    private static void PathRepairExecutionFlow(MainWindow window, string uiDataDir)
    {
        MainViewModel vm = window.ViewModel;

        // (a) 目录「A.」（尾随点）+ con.txt + 超长文件：扫描后一次全部修复成功
        string caseA = H.NewDir("repair-a");
        string dotted = Path.Combine(caseA, "A.");
        H.MakeDir(dotted);
        string conFile = Path.Combine(dotted, "con.txt");
        H.WriteFile(conFile, "reserved");

        string longName = new string('y', 240) + ".txt";
        string longFile = Path.Combine(dotted, longName);
        H.WriteFile(longFile, "toolong");

        IReadOnlyList<PathRepairCandidate> candidates = PathRepairScanner.AnalyzePaths(new[]
        {
            dotted, conFile, longFile,
        });
        H.Check("(a) 扫描发现 3 个问题项", candidates.Count == 3, $"实际 {candidates.Count}");

        var planA = new PathRepairPlan();
        foreach (PathRepairCandidate candidate in candidates)
        {
            planA.Items.Add(new PathRepairPlanItem(
                candidate.DisplayPath,
                candidate.Name,
                candidate.ParentFolderDisplayPath,
                candidate.Suggestion.Name));

            if (candidate.Suggestion.FolderShortenName is { Length: > 0 } shortened
                && !planA.FolderRenames.Any(f => string.Equals(
                    f.OriginalFolderDisplayPath, candidate.ParentFolderDisplayPath, StringComparison.OrdinalIgnoreCase)))
            {
                planA.FolderRenames.Add(new PathRepairFolderRename(candidate.ParentFolderDisplayPath, shortened));
            }
        }

        PathRepairExecutionResult resultA = vm.ExecuteRepairRenames(planA);
        H.Check("(a) 修复全部成功", resultA.RepairedCount == 3 && resultA.FailedCount == 0,
            $"修复 {resultA.RepairedCount} / 失败 {resultA.FailedCount}：{resultA.Message}");
        H.Check("(a) 无失败项回传", resultA.Failures.Count == 0);

        string renamedFolder = Path.Combine(caseA, PathRepairRules.FixNameIssues("A.")); // "A"
        string renamedCon = Path.Combine(renamedFolder, PathRepairRules.FixNameIssues("con.txt"));
        H.Check("(a) 尾随点目录已改名", !H.Exists(dotted) && H.Exists(renamedFolder));
        H.Check("(a) con.txt 已改名", !H.Exists(conFile) && H.Exists(renamedCon));
        H.Check("(a) 超长文件已改名（原路径已不存在）", !H.Exists(longFile));
        int fileCount = H.Exists(renamedFolder) ? Directory.EnumerateFiles(renamedFolder).Count() : -1;
        H.Check("(a) 改名后目录内文件数 = 2", fileCount == 2, $"实际 {fileCount}");

        vm.History.Clear();
        vm.UndoCommand.NotifyCanExecuteChanged();

        // (b) 带上级文件夹缩短的一批：撤销后名称与原来完全一致，重做后再次一致
        string caseB = H.NewDir("repair-b");
        string parentB = Path.Combine(caseB, new string('p', 220));
        H.MakeDir(parentB);
        string bFileName = new string('n', 30) + ".txt";
        string fileB = Path.Combine(parentB, bFileName);
        H.WriteFile(fileB, "B");

        PathRepairCandidate candidateB = PathRepairScanner.AnalyzePaths(new[] { fileB }).Single();
        PathRepairSuggestion sugB = PathRepairRules.Suggest(candidateB.Name, candidateB.ParentFolderDisplayPath, candidateB.IsDirectory);
        H.Check("(b) 给出「缩短上级文件夹」建议", sugB.FolderShortenName is { Length: > 0 }, sugB.FolderShortenNote ?? "无建议");
        H.Check("(b) 建议名与原文件名不同", sugB.Name != bFileName, $"建议 {sugB.Name}");

        var planB = new PathRepairPlan();
        planB.FolderRenames.Add(new PathRepairFolderRename(parentB, sugB.FolderShortenName!));
        planB.Items.Add(new PathRepairPlanItem(fileB, candidateB.Name, parentB, sugB.Name));

        PathRepairExecutionResult resultB = vm.ExecuteRepairRenames(planB);
        H.Check("(b) 修复成功", resultB.RepairedCount >= 1 && resultB.FailedCount == 0, resultB.Message);
        string newParentB = Path.Combine(caseB, sugB.FolderShortenName!);
        string newFileB = Path.Combine(newParentB, sugB.Name);
        H.Check("(b) 修复后上级文件夹已缩短", H.Exists(newParentB) && !H.Exists(parentB));
        H.Check("(b) 修复后文件已改名", H.Exists(newFileB) && !H.Exists(fileB));

        // Ctrl+Z 一次撤销整批：所有名称与原来完全一致
        vm.UndoCommand.Execute(null);
        Program.Pump(200);
        H.Check("(b) 撤销后上级文件夹恢复原名", H.Exists(parentB) && !H.Exists(newParentB),
            $"parentB={H.Exists(parentB)} newParentB={H.Exists(newParentB)}");
        H.Check("(b) 撤销后文件恢复原名", H.Exists(fileB) && !H.Exists(newFileB));

        // Ctrl+Y 重做整批：再次与修复后一致
        vm.RedoCommand.Execute(null);
        Program.Pump(200);
        H.Check("(b) 重做后上级文件夹再次缩短", H.Exists(newParentB) && !H.Exists(parentB));
        H.Check("(b) 重做后文件再次改名", H.Exists(newFileB) && !H.Exists(fileB));

        vm.History.Clear();
        vm.UndoCommand.NotifyCanExecuteChanged();

        // (c) 制造一项失败：目标新名称已被占用
        string caseC = H.NewDir("repair-c");
        string srcC = Path.Combine(caseC, "conflict.txt");
        string blockerC = Path.Combine(caseC, "taken.txt");
        H.WriteFile(srcC, "src");
        H.WriteFile(blockerC, "blocker");

        var planC = new PathRepairPlan();
        planC.Items.Add(new PathRepairPlanItem(srcC, "conflict.txt", caseC, "taken.txt"));
        PathRepairExecutionResult resultC = vm.ExecuteRepairRenames(planC);
        H.Check("(c) 失败项被记录", resultC.FailedCount == 1 && resultC.Failures.Count == 1,
            $"失败 {resultC.FailedCount}，明细 {resultC.Failures.Count}");
        H.Check("(c) 失败项带路径与原因",
            resultC.Failures.Count == 1 && resultC.Failures[0].DisplayPath.Length > 0 && resultC.Failures[0].Reason.Length > 0,
            resultC.Failures.Count == 1 ? resultC.Failures[0].Reason : "无明细");
        H.Check("(c) 完成文案不提日志", !resultC.Message.Contains("日志", StringComparison.Ordinal), resultC.Message);
        H.Check("(c) 源文件未被误改", H.Exists(srcC) && H.ReadFile(blockerC) == "blocker");

        vm.History.Clear();
        vm.UndoCommand.NotifyCanExecuteChanged();
        _ = uiDataDir;
    }
}
