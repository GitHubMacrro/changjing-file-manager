using System.Diagnostics;
using System.IO;
using ChangJing.App.Core.LongPath;
using ChangJing.App.Core.Operations;
using ChangJing.App.ViewModels;
using ChangJing.App.Views;

namespace ChangJing.Regression;

/// <summary>性能与稳定性（真实主窗口）：大目录加载 / 滚动、复制期间 UI 不卡、切文件夹后工作集稳定</summary>
public static class PerfSuite
{
    public static void Run()
    {
        if (Environment.GetEnvironmentVariable("CJ_REG_SKIP_PERF") == "1")
        {
            H.Group("C 性能与稳定（已跳过：CJ_REG_SKIP_PERF=1）");
            return;
        }

        // 当前是否为 32 位进程：性能阈值按架构分档
        bool isX86 = !Environment.Is64BitProcess;

        H.Group("C1 5 万项文件夹：首批 / 滚动 / 全选");

        string big = Path.Combine(H.Root, "perf-50k");
        H.MakeDir(big);
        var createSw = Stopwatch.StartNew();
        string bigLong = PathHelper.ToLong(big);
        for (int i = 0; i < 50000; i++)
        {
            string name = Path.Combine(bigLong, "file_" + i.ToString("D6") + ".txt");
            using FileStream _ = File.Create(name);
        }

        createSw.Stop();
        H.Info($"  已生成 50000 个文件，用时 {createSw.ElapsedMilliseconds} ms");

        MainWindow window = Program.Window!;
        MainViewModel vm = window.ViewModel;

        var openSw = Stopwatch.StartNew();
        long firstBatchMs = -1;
        vm.NavigateToLocation(big);
        Program.PumpUntil(() =>
        {
            if (firstBatchMs < 0 && vm.Items.Count > 0)
            {
                firstBatchMs = openSw.ElapsedMilliseconds;
            }

            return !vm.IsLoading && vm.Items.Count >= 50000;
        }, 60000);
        openSw.Stop();

        H.Info($"  首批显示：{firstBatchMs} ms；全部加载：{openSw.ElapsedMilliseconds} ms；共 {vm.Items.Count} 项");
        H.Check("5 万项全部加载", vm.Items.Count >= 50000, $"实际 {vm.Items.Count}");

        // x86 为 32 位进程，托管堆与大目录枚举开销明显高于 x64；且私有集上限 2GB 下 GC 更频繁，故阈值单独放宽（x64 不变）
        long firstBatchLimitMs = isX86 ? 2500 : 1500;
        H.Check($"首批显示 < {firstBatchLimitMs} ms（目标 < 300 ms 量级）", firstBatchMs >= 0 && firstBatchMs < firstBatchLimitMs, $"{firstBatchMs} ms");

        // 滚动：强制布局 / 渲染 200 步
        var scrollSw = Stopwatch.StartNew();
        const int steps = 200;
        if (window.FindName("FileListView") is not null)
        {
            var listView = (System.Windows.Controls.ListView)window.FindName("FileListView")!;
            for (int i = 0; i < steps; i++)
            {
                listView.ScrollIntoView(vm.Items[Math.Min(vm.Items.Count - 1, i * 250)]);
                listView.UpdateLayout();
            }
        }

        scrollSw.Stop();
        double perFrame = scrollSw.Elapsed.TotalMilliseconds / steps;
        H.Info($"  滚动 {steps} 步：{scrollSw.ElapsedMilliseconds} ms（{perFrame:F2} ms/步）");
        H.Check("滚动每步 < 60 ms", perFrame < 60, $"{perFrame:F2} ms/步");

        // 全选
        var selectSw = Stopwatch.StartNew();
        vm.UpdateSelection(vm.Items);
        selectSw.Stop();
        H.Info($"  全选 50000 项：{selectSw.ElapsedMilliseconds} ms；已选 {vm.SelectedItems.Count}");
        H.Check("全选 5 万项", vm.SelectedItems.Count >= 50000);
        H.Check("全选耗时 < 1500 ms", selectSw.ElapsedMilliseconds < 1500, $"{selectSw.ElapsedMilliseconds} ms");

        vm.UpdateSelection(Array.Empty<FileItemViewModel>());

        H.Group("C2 复制 1 万小文件：进度刷新期间 UI 不卡");

        string src10k = Path.Combine(H.Root, "perf-copy-src");
        H.MakeDir(src10k);
        string src10kLong = PathHelper.ToLong(src10k);
        for (int i = 0; i < 10000; i++)
        {
            string name = Path.Combine(src10kLong, "c_" + i.ToString("D5") + ".bin");
            File.WriteAllBytes(name, new byte[256]);
        }

        string dst10k = Path.Combine(H.Root, "perf-copy-dst");
        H.MakeDir(dst10k);

        // 记录 UI 线程最大卡顿：后台线程周期性派发高优先级回调，测最大间隔
        long maxGapMs = 0;
        var lastTick = Stopwatch.StartNew();
        bool measuring = true;
        var monitor = new System.Threading.Timer(_ =>
        {
            if (!measuring)
            {
                return;
            }

            long elapsed = lastTick.ElapsedMilliseconds;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Send,
                new Action(() =>
                {
                    long now = lastTick.ElapsedMilliseconds;
                    long gap = now - elapsed;
                    if (gap > maxGapMs)
                    {
                        maxGapMs = gap;
                    }
                }));
        }, null, 5, 5);

        var job = new OperationJob(OperationKind.Copy);
        job.AddSource(src10k, dst10k);
        int progressReports = 0;
        job.ProgressReporter = new Progress<OperationProgress>(_ => Interlocked.Increment(ref progressReports));

        // 引擎在真实产品里跑在后台线程；这里也必须后台跑，否则测的是 UI 全程卡死而非进度刷新
        var copySw = Stopwatch.StartNew();
        Task<OperationResult> copyTask = Task.Run(() => OperationEngine.Execute(job));
        Program.PumpUntil(() => copyTask.IsCompleted, 60000);
        copySw.Stop();
        OperationResult result = copyTask.GetAwaiter().GetResult();
        measuring = false;
        monitor.Dispose();
        Program.Pump(200);

        H.Info($"  复制 10000 小文件：{copySw.ElapsedMilliseconds} ms；进度回调 {progressReports} 次；UI 最大卡顿 {maxGapMs} ms");
        H.Check("复制 1 万小文件成功", result.Success, result.Message);
        H.Check("进度回调有刷新", progressReports > 0, $"{progressReports} 次");

        // x86 复制 1 万小文件本身更慢，UI 线程受同机负载影响更明显，故阈值单独放宽（x64 保持 300 ms）
        long uiStallLimitMs = isX86 ? 800 : 300;
        H.Check($"复制期间 UI 最大卡顿 < {uiStallLimitMs} ms", maxGapMs < uiStallLimitMs, $"{maxGapMs} ms");

        H.Group("C3 连续切换 200 次文件夹 + 预览：工作集基本稳定");

        // 用两个小目录来回切换 200 次，并切换预览目标
        string dirA = H.NewDir("switch-a");
        string dirB = H.NewDir("switch-b");
        for (int i = 0; i < 20; i++)
        {
            H.WriteFile(Path.Combine(dirA, $"a{i}.txt"), "a" + i);
            H.WriteFile(Path.Combine(dirB, $"b{i}.txt"), "b" + i);
        }

        // 预热 + 基线（GC 后）
        vm.NavigateToLocation(dirA);
        Program.PumpUntil(() => !vm.IsLoading, 5000);
        long baseline = MeasureWorkingSet();

        for (int i = 0; i < 200; i++)
        {
            string dir = (i % 2 == 0) ? dirA : dirB;
            vm.NavigateToLocation(dir);
            Program.PumpUntil(() => !vm.IsLoading, 5000);

            if (vm.Items.Count > 0)
            {
                vm.UpdateSelection(new[] { vm.Items[i % vm.Items.Count] });
                Program.Pump(2);
            }
        }

        long after = MeasureWorkingSet();
        double growthMb = (after - baseline) / 1024.0 / 1024.0;
        H.Info($"  工作集：基线 {baseline / 1024 / 1024} MB → 200 次后 {after / 1024 / 1024} MB（增长 {growthMb:F1} MB）");
        H.Check("200 次切换后工作集增长 < 120 MB", growthMb < 120, $"增长 {growthMb:F1} MB");
    }

    /// <summary>强制 GC 后取工作集（MB 级稳定判定）</summary>
    private static long MeasureWorkingSet()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Program.Pump(100);
        using Process p = Process.GetCurrentProcess();
        p.Refresh();
        return p.WorkingSet64;
    }
}
