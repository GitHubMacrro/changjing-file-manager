using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using ChangJing.App.Core.Settings;
using ChangJing.App.Services;
using ChangJing.App.Views;
using CustomWin.Utils.Logs;

namespace ChangJing.App;

/// <summary>应用入口：初始化日志、捕获异常、组装服务、应用主题、显示主窗口</summary>
public partial class App : Application
{
    /// <summary>供安装程序检测「程序正在运行」的命名互斥体名，默认 Local 作用域</summary>
    public const string RunningMutexName = "ChangJing.App.Running";

    private AppServices? _services;

    // 全程持有，仅作运行中标记；不做单实例限制，每次启动仍开新进程
    private Mutex? _runningMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        InitializeLogging();

        // 尽早创建运行中互斥体，供安装程序检测；并不限制单实例
        AcquireRunningMutex();

        RegisterGlobalExceptionHandlers();

        // 强制退出路径的兜底清理：删短根联接、清临时副本
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupLongPathOpen();

        LogStartup();

        // 放后台线程，磁盘 / 注册表操作不阻塞开窗
        _ = Task.Run(OpenCleanup.SweepStartup);

        _services = new AppServices(new SettingsService(), new ThemeService());

        AppSettings settings = _services.SettingsService.Load();

        _services.ThemeService.Initialize(settings.ThemeMode);

        // 提权交接优先：--folder <json> 给出文件夹时以其为准，否则用位置参数
        string? startupFolder = ElevationService.ConsumeStartupFolder(e.Args);
        StartupTarget target = string.IsNullOrEmpty(startupFolder)
            ? CommandLineStartup.Resolve(e.Args)
            : StartupTarget.ForFolder(startupFolder!);

        var window = new MainWindow(_services, settings, recycleBinOptions: null, startupTarget: target);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            CleanupLongPathOpen();
            LogManager.Info(typeof(App), "应用退出");
            LogManager.Flush();
            LogManager.Shutdown();
        }
        catch
        {
            // 退出路径上的日志失败不影响进程退出码
        }

        ReleaseRunningMutex();

        base.OnExit(e);
    }

    /// <summary>创建并持有命名互斥体，仅用于安装程序检测运行中；失败不阻断启动</summary>
    private void AcquireRunningMutex()
    {
        try
        {
            _runningMutex = new Mutex(initiallyOwned: false, RunningMutexName);
            LogManager.Info(typeof(App), $"已创建运行中互斥体：{RunningMutexName}");
        }
        catch (Exception ex)
        {
            // 互斥体创建失败不影响程序运行
            LogManager.Warning(typeof(App), $"创建运行中互斥体失败：{ex.Message}");
            _runningMutex = null;
        }
    }

    private void ReleaseRunningMutex()
    {
        try
        {
            _runningMutex?.Dispose();
        }
        catch
        {
            // 退出路径忽略
        }
        finally
        {
            _runningMutex = null;
        }
    }

    private static void CleanupLongPathOpen()
    {
        OpenCleanup.CleanupAll();
    }

    private static void InitializeLogging()
    {
        try
        {
            LogManager.LogDirectory = AppPaths.LogsDirectory;   // setter 会建目录
            LogManager.LogFileName = "changjing";

            // 单文件上限 10 MB，超限自动切到 changjing(0).log / changjing(1).log …
            LogManager.EnableFileLimit = true;
            LogManager.LimitSize = 10;

            LogManager.PrintLogToConsole = false;
            LogManager.EnableCallerMemberName = true;
            LogManager.LevelFormatOption = LevelFormat.Short;

            LogManager.OnWriteError += error =>
            {
                // 写日志失败无更好去处，静默忽略
            };

            LogMaintenance.Prune(AppPaths.LogsDirectory, "changjing");
        }
        catch
        {
            // 日志初始化失败不阻断启动
        }
    }

    private static void LogStartup()
    {
        try
        {
            string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
            LogManager.Info(
                typeof(App),
                $"应用启动：版本 {version}，OS {Environment.OSVersion.Version}，日志目录 {AppPaths.LogsDirectory}");
        }
        catch
        {
        }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorReporter.ReportUnhandled(e.Exception, "界面线程");
        e.Handled = true;   // 继续运行
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception
            ?? new InvalidOperationException($"非异常对象的未处理错误：{e.ExceptionObject}");

        // 非界面线程未处理异常默认会终止进程，弹窗可能来不及显示
        ErrorReporter.ReportUnhandled(exception, $"后台线程（即将终止：{e.IsTerminating}）");

        try
        {
            LogManager.Flush();
        }
        catch
        {
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ErrorReporter.ReportUnhandled(e.Exception, "后台任务");
        e.SetObserved();    // 避免因未观察异常升级为进程终止
    }
}
