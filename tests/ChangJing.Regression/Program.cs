using System.Windows;
using System.Windows.Threading;
using ChangJing.App.Views;

namespace ChangJing.Regression;

/// <summary>回归 harness 入口：控制台 + WPF 进程内，用真实 MainWindow 与真实主题</summary>
public static class Program
{
    /// <summary>WPF 应用，ShutdownMode=OnExplicitShutdown</summary>
    public static Application? App { get; private set; }

    /// <summary>主窗口，整个 harness 复用一个</summary>
    public static MainWindow? Window { get; set; }

    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        H.Initialize();

        H.Info($"运行架构：Is64BitProcess={Environment.Is64BitProcess}，Is64BitOperatingSystem={Environment.Is64BitOperatingSystem}");

        // 真实产品资源：App.xaml 合并 WPF-UI + 畅径主题 + 调色板 + 转换器
        var app = new ChangJing.App.App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        App = app;

        int exitCode;
        try
        {
            CoreSuite.Run();
            UiSuite.Run();
            SelectionSuite.Run();
            PublishSuiteRunner.Run();
            PerfSuite.Run();
            exitCode = H.FailureList.Count == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            H.Info($"[致命] harness 顶层异常：{ex}");
            exitCode = 2;
        }
        finally
        {
            try
            {
                Window?.Close();
            }
            catch
            {
                // 忽略
            }

            PrintSummary();
            H.Cleanup();
        }

        return exitCode;
    }

    private static void PrintSummary()
    {
        Console.WriteLine();
        Console.WriteLine("==================== 回归结果 ====================");
        Console.WriteLine($"断言总数：{H.Total}    通过：{H.Passed}    失败：{H.FailureList.Count}");
        if (H.FailureList.Count > 0)
        {
            Console.WriteLine("---------------- 失败明细 ----------------");
            foreach (string failure in H.FailureList)
            {
                Console.WriteLine("  · " + failure);
            }
        }

        Console.WriteLine("日志：" + H.LogPath);
        Console.WriteLine("=================================================");
    }

    // 消息泵辅助

    /// <summary>运行 WPF 消息泵 <paramref name="milliseconds"/> 毫秒</summary>
    public static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(Math.Max(1, milliseconds)),
            DispatcherPriority.Background,
            (_, _) => frame.Continue = false,
            Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    /// <summary>泵消息直到条件成立或超时；返回条件是否成立</summary>
    public static bool PumpUntil(Func<bool> condition, int timeoutMs = 10000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            Pump(25);
        }

        return condition();
    }
}
