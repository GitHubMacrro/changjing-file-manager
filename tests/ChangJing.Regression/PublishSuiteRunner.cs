using System.Diagnostics;
using System.IO;
using System.Windows;
using ChangJing.App.Core.PathRepair;
using ChangJing.App.Core.RecycleBin;
using ChangJing.App.Core.Settings;
using ChangJing.App.Services;
using ChangJing.App.ViewModels;
using ChangJing.App.Views;

namespace ChangJing.Regression;

/// <summary>公开发布前套件：工具栏按钮 / 关于页断言 + docs\images 截图生成</summary>
// 演示数据只落在 %TEMP%\cj-demo-*；用 subst 映射一个盘符，使截图里的路径不含用户名 / 本机私有信息
public static class PublishSuiteRunner
{
    private static readonly string DemoRoot = Path.Combine(Path.GetTempPath(), "cj-demo-" + Guid.NewGuid().ToString("N")[..6]);

    private static string? _mappedDrive;

    private static string _deepestDir = string.Empty;

    /// <summary>截图输出目录（仓库 docs\images）</summary>
    public static string ImagesDirectory { get; } = ResolveImagesDirectory();

    public static void Run()
    {
        bool captured = false;
        try
        {
            PrepareDemoData();
            _mappedDrive = TryMapDrive(DemoRoot);

            string dataDir = _mappedDrive is not null ? _mappedDrive + @"\demo" : Path.Combine(DemoRoot, "demo");

            RunChecksAndScreenshots(dataDir);
            captured = true;
        }
        catch (Exception ex)
        {
            H.Check("公开发布套件执行完成", false, ex.Message);
            H.Info($"[PublishSuite] 异常：{ex}");
        }
        finally
        {
            if (_mappedDrive is not null)
            {
                TryUnmapDrive(_mappedDrive);
            }

            if (captured)
            {
                H.Info($"  截图输出目录：{ImagesDirectory}");
            }
        }
    }

    private static void RunChecksAndScreenshots(string dataDir)
    {
        string longDir = _mappedDrive is not null
            ? _deepestDir.Replace(DemoRoot, _mappedDrive, StringComparison.OrdinalIgnoreCase)
            : _deepestDir;
        // 隔离设置 + 浅色主题（公开截图统一浅色）
        var settingsService = new SettingsService(Path.Combine(H.Root, "publish-settings.json"));
        AppSettings settings = settingsService.Load();
        var themeService = new ThemeService();
        themeService.Initialize(AppSettings.ThemeModeLight);
        var services = new AppServices(settingsService, themeService);

        string recycleRoot = _mappedDrive is not null ? _mappedDrive + @"\$CJ.DemoRecycle" : Path.Combine(DemoRoot, "recycle");
        var recycleOptions = new RecycleBinOptions
        {
            RootOverride = _ => recycleRoot,
            VolumeRootsOverride = () => _mappedDrive is not null ? new[] { _mappedDrive + "\\" } : Array.Empty<string>(),
        };

        MainWindow window;
        try
        {
            window = new MainWindow(services, settings, recycleOptions);
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000;
            window.Top = -32000;
            window.Show();
        }
        catch (Exception ex)
        {
            H.Check("公开套件：主窗口创建", false, ex.Message);
            return;
        }

        try
        {
            Program.PumpUntil(() => window.IsLoaded, 8000);
            Program.Pump(300);

            // 断言（含工具栏按钮可见性 + 关于页）
            PublishSuite.Run(window, settings, dataDir);

            // 截图
            CaptureMain(window, dataDir);
            CaptureLongPath(window, longDir);
            CaptureRecycleBin(window, settings, dataDir, recycleRoot);
            CaptureAbout(window, settings);
            CapturePathRepair(window, dataDir);
        }
        finally
        {
            try
            {
                window.Close();
                Program.Pump(200);
            }
            catch
            {
                // 忽略
            }
        }
    }

    // 截图

    private static void CaptureMain(MainWindow window, string dataDir)
    {
        Visit(window.ViewModel, dataDir);
        Program.Pump(300);
        SaveWindow(window, "main.png");
    }

    private static void CaptureLongPath(MainWindow window, string longDir)
    {
        Visit(window.ViewModel, longDir);
        Program.Pump(300);
        SaveWindow(window, "longpath-folder.png");
    }

    private static void CaptureRecycleBin(MainWindow window, AppSettings settings, string dataDir, string recycleRoot)
    {
        // 先把演示文件移入回收站（用真实存储逻辑），再进入回收站页截图
        var store = new RecycleBinStore(new RecycleBinOptions { RootOverride = _ => recycleRoot });
        foreach (string name in new[] { "旧报告.txt", "草稿.md" })
        {
            string path = Path.Combine(dataDir, name);
            if (File.Exists(path))
            {
                store.MoveIn(path);
            }
        }

        Visit(window.ViewModel, MainViewModel.RecycleBinToken);
        Program.PumpUntil(() => !window.ViewModel.IsLoading, 10000);
        Program.Pump(400);
        SaveWindow(window, "recyclebin.png");
        _ = settings;
    }

    private static void CaptureAbout(MainWindow window, AppSettings settings)
    {
        try
        {
            var dialog = new SettingsDialog(new SettingsViewModel(settings))
            {
                Owner = window,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
            };
            dialog.Show();
            Program.PumpUntil(() => dialog.IsLoaded, 5000);
            dialog.SelectAboutTab();
            Program.Pump(400);
            SaveWindow(dialog, "about.png");
            dialog.Close();
            Program.Pump(150);
        }
        catch (Exception ex)
        {
            H.Info($"  [截图失败] about.png：{ex.Message}");
        }
    }

    private static void CapturePathRepair(MainWindow window, string dataDir)
    {
        try
        {
            string repairDir = Path.Combine(dataDir, "待修复");
            Directory.CreateDirectory(repairDir);
            WriteViaLongPath(Path.Combine(repairDir, "con.txt"), "reserved");
            WriteViaLongPath(Path.Combine(repairDir, "readme .txt"), "trailing space");
            WriteViaLongPath(Path.Combine(repairDir, "trailing."), "trailing dot");

            var request = new PathRepairRequestEventArgs(Array.Empty<string>(), repairDir);
            var vm = new PathRepairViewModel(request, window.ViewModel, window.ViewModel.DefaultRepairMoveTargetPath);
            var dialog = new PathRepairDialog(vm, _ => { })
            {
                Owner = window,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
            };

            dialog.Show();
            Program.PumpUntil(() => dialog.IsLoaded, 5000);
            Program.PumpUntil(() => !vm.IsScanning, 8000);
            Program.Pump(300);
            SaveWindow(dialog, "repair-dialog.png");
            dialog.Close();
            Program.Pump(150);
        }
        catch (Exception ex)
        {
            H.Info($"  [截图失败] repair-dialog.png：{ex.Message}");
        }
    }

    private static void SaveWindow(Window window, string fileName)
    {
        string path = Path.Combine(ImagesDirectory, fileName);
        (int Width, int Height)? size = ScreenshotCapture.Save(window, path);
        if (size is { } s)
        {
            H.Info($"  [截图] {fileName}（{s.Width}x{s.Height}）");
        }
    }

    // 演示数据

    private static void PrepareDemoData()
    {
        Directory.CreateDirectory(DemoRoot);

        string demo = Path.Combine(DemoRoot, "demo");
        Directory.CreateDirectory(demo);
        File.WriteAllText(Path.Combine(demo, "项目说明.txt"), "畅径演示文件");
        File.WriteAllText(Path.Combine(demo, "会议记录.txt"), "会议记录演示");
        File.WriteAllText(Path.Combine(demo, "旧报告.txt"), "旧报告演示");
        File.WriteAllText(Path.Combine(demo, "草稿.md"), "草稿演示");
        File.WriteAllText(Path.Combine(demo, "数据.csv"), "a,b,c\n1,2,3");
        File.WriteAllText(Path.Combine(demo, "alpha.txt"), "alpha");
        Directory.CreateDirectory(Path.Combine(demo, "子文件夹"));

        // 长路径目录树：每段 30 字符，逐层加深到超过 260
        string current = Path.Combine(DemoRoot, "longpath");
        Directory.CreateDirectory(current);
        for (int i = 0; i < 12; i++)
        {
            string segment = "深层文件夹" + i.ToString("D2") + new string('长', 20);
            current = Path.Combine(current, segment);
            Directory.CreateDirectory(ToLongPath(current));
        }

        File.WriteAllText(ToLongPath(Path.Combine(current, "很深的文件.txt")), "深路径演示");
        _deepestDir = current;
    }

    private static void WriteViaLongPath(string displayPath, string content)
    {
        string longPath = ToLongPath(displayPath);
        string? parent = Path.GetDirectoryName(longPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.WriteAllText(longPath, content);
    }

    private static string ToLongPath(string displayPath)
        => displayPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? displayPath : @"\\?\" + displayPath;

    private static void Visit(MainViewModel vm, string location)
    {
        vm.NavigateToLocation(location);
        Program.PumpUntil(() => !vm.IsLoading, 12000);
        Program.Pump(200);
    }

    // subst 盘符映射

    private static string? TryMapDrive(string target)
    {
        foreach (char letter in "XYZWVUTSRQ")
        {
            string drive = letter + ":";
            if (Directory.Exists(drive + "\\"))
            {
                continue;
            }

            if (RunSubst($"{drive} \"{target}\"") && Directory.Exists(drive + "\\"))
            {
                H.Info($"  [subst] 已映射 {drive} → {target}");
                return drive;
            }
        }

        H.Info("  [subst] 未能映射盘符，截图路径可能包含用户名。");
        return null;
    }

    private static void TryUnmapDrive(string drive)
    {
        RunSubst($"{drive} /D");
    }

    private static bool RunSubst(string arguments)
    {
        try
        {
            var info = new ProcessStartInfo("subst", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using Process? process = Process.Start(info);
            process?.WaitForExit(5000);
            return process is { ExitCode: 0 };
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveImagesDirectory()
    {
        // 从可执行文件目录往上找仓库根（含 ChangJing.sln）
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "ChangJing.sln")))
            {
                return Path.Combine(dir, "docs", "images");
            }

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        return Path.Combine(Path.GetTempPath(), "cj-docs-images");
    }
}
