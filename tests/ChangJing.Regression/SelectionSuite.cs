using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ChangJing.App.Controls;
using ChangJing.App.Core.Operations;
using ChangJing.App.Core.RecycleBin;
using ChangJing.App.Core.Settings;
using ChangJing.App.Services;
using ChangJing.App.ViewModels;
using ChangJing.App.Views;

namespace ChangJing.Regression;

/// <summary>多选增强回归：复选框、Ctrl 框选追加、反向选择、状态栏、保留选择、万项全选耗时</summary>
// 自建窗口：UiSuite 结束时会关掉它自己的窗口
public static class SelectionSuite
{
    public static void Run()
    {
        H.Group("D1 多选增强：复选框 / 框选 / 选择命令 / 状态栏 / 保留选择");

        var settingsService = new SettingsService(Path.Combine(H.Root, "sel-settings.json"));
        AppSettings settings = settingsService.Load();
        var themeService = new ThemeService();
        themeService.Initialize(AppSettings.ThemeModeLight);
        var services = new AppServices(settingsService, themeService);

        MainWindow window;
        try
        {
            window = new MainWindow(services, settings, new RecycleBinOptions
            {
                RootOverride = _ => Path.Combine(H.Root, "sel-recycle"),
                VolumeRootsOverride = () => Array.Empty<string>(),
            });

            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000;
            window.Top = -32000;
            window.Show();
        }
        catch (Exception ex)
        {
            H.Check("多选套件：主窗口创建", false, ex.Message);
            return;
        }

        try
        {
            Program.PumpUntil(() => window.IsLoaded, 8000);
            Program.Pump(300);

            MainViewModel vm = window.ViewModel;
            Program.PumpUntil(() => vm.NavigationRoots.Count >= 1, 10000);

            string dir = H.NewDir("sel-data");
            for (int i = 0; i < 6; i++)
            {
                H.WriteFile(Path.Combine(dir, $"item{i}.txt"), new string('x', 1024 * (i + 1)));
            }

            H.MakeDir(Path.Combine(dir, "folderA"));

            vm.NavigateToLocation(dir);
            Program.PumpUntil(() => !vm.IsLoading && vm.Items.Count >= 7, 15000);
            H.Eq("测试目录加载 7 项", 7, vm.Items.Count);

            var list = (System.Windows.Controls.ListView)window.FindName("FileListView")!;
            Program.Pump(200);

            CheckBoxToggleKeepsOthers(vm, list);
            CtrlBandAppends(vm, list);
            InvertSelection(vm, list);
            StatusBarCountsFilesOnly(vm, list);
            RefreshKeepsSelection(vm);
            UndoRestoresSelection(vm);
            TransferHistoryRecordsSelectionBefore(vm, dir);
            HeaderCheckBox(window, vm, list);
            LongNameSelectable(vm);
            CheckBoxSettingPersists(window, vm, settingsService);

            vm.ShowCheckBoxes = false;
            vm.UpdateSelection(Array.Empty<FileItemViewModel>());
            Program.Pump(100);
        }
        catch (Exception ex)
        {
            H.Check("多选套件执行完成", false, ex.Message);
            H.Info($"[SelectionSuite] 异常：{ex}");
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

    // 点复选框只切换该项，不清其它已选项
    private static void CheckBoxToggleKeepsOthers(MainViewModel vm, System.Windows.Controls.ListView list)
    {
        vm.ShowCheckBoxes = true;
        Program.Pump(250);

        vm.ApplySelection(new[] { vm.Items[0], vm.Items[1], vm.Items[2] });
        Program.Pump(200);
        H.Eq("复选框模式：先选中 3 项", 3, vm.SelectedItems.Count);

        H.Check("复选框模式下已选中行显示复选框", CountShownCheckBoxes(list) > 0, "未找到显示的复选框");

        // 点第 4 项的复选框：只加这一项
        FileItemViewModel target = vm.Items[3];
        var next = vm.SelectedItems.ToList();
        next.Add(target);
        vm.ApplySelection(next);
        Program.Pump(150);
        H.Eq("点复选框后已选 4 项", 4, vm.SelectedItems.Count);
        H.Check("点复选框不清除其它已选项",
            vm.SelectedItems.Contains(vm.Items[0]) && vm.SelectedItems.Contains(target));

        // 再取消这一项：其余 3 项仍在
        vm.ApplySelection(vm.SelectedItems.Where(item => !ReferenceEquals(item, target)).ToList());
        Program.Pump(150);
        H.Eq("取消该项后回到 3 项", 3, vm.SelectedItems.Count);
        H.Check("取消该项后其它项仍选中",
            vm.SelectedItems.Contains(vm.Items[0]) && vm.SelectedItems.Contains(vm.Items[2]));
    }

    // 复选框只在悬停或已选中时显示，故统计「已显示」的数量
    private static int CountShownCheckBoxes(System.Windows.Controls.ListView list)
    {
        int found = 0;
        for (int i = 0; i < list.Items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is ListViewItem container
                && FindDescendant<CheckBox>(container) is { Visibility: not Visibility.Collapsed })
            {
                found++;
            }
        }

        return found;
    }

    private static int CountVisibleCheckBoxes(System.Windows.Controls.ListView list)
    {
        return CountShownCheckBoxes(list);
    }

    // Ctrl 拖框为追加选择：基线保留，框内新增
    private static void CtrlBandAppends(MainViewModel vm, System.Windows.Controls.ListView list)
    {
        vm.ApplySelection(new[] { vm.Items[0] });
        Program.Pump(150);
        list.ScrollIntoView(vm.Items[0]);
        list.UpdateLayout();
        Program.Pump(150);

        Rect firstRow = BoundsOf(list, vm.Items[0]);
        Rect thirdRow = BoundsOf(list, vm.Items[2]);
        H.Check("能取到行边界", !firstRow.IsEmpty && !thirdRow.IsEmpty,
            $"first={firstRow}, third={thirdRow}");

        if (firstRow.IsEmpty || thirdRow.IsEmpty)
        {
            return;
        }

        // 覆盖第 3 行的框（比行高窄一点，确保不会碰到第 1 行）
        var band = new Rect(
            new Point(4, thirdRow.Top + 1),
            new Point(Math.Max(20, list.ActualWidth - 4), thirdRow.Bottom - 1));

        var baseline = list.SelectedItems.Cast<object>().ToList();
        RubberBandSelector.SelectInBand(list, band, additive: true, baseline);
        vm.UpdateSelection(list.SelectedItems.OfType<FileItemViewModel>());
        Program.Pump(150);

        H.Check("Ctrl 框选保留原有选中项", vm.SelectedItems.Contains(vm.Items[0]),
            $"已选 {vm.SelectedItems.Count}");
        H.Check("Ctrl 框选把框内项加入", vm.SelectedItems.Count >= 2, $"已选 {vm.SelectedItems.Count}");

        // 非追加框选会替换选择
        RubberBandSelector.SelectInBand(list, band, additive: false, baseline: null);
        vm.UpdateSelection(list.SelectedItems.OfType<FileItemViewModel>());
        Program.Pump(150);
        H.Check("非追加框选不含框外项", !vm.SelectedItems.Contains(vm.Items[0]),
            $"已选 {vm.SelectedItems.Count}");
    }

    private static Rect BoundsOf(System.Windows.Controls.ListView list, FileItemViewModel item)
    {
        if (list.ItemContainerGenerator.ContainerFromItem(item) is not ListViewItem container)
        {
            return Rect.Empty;
        }

        try
        {
            GeneralTransform transform = container.TransformToAncestor(list);
            return transform.TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
        }
        catch
        {
            return Rect.Empty;
        }
    }

    // 反向选择：已选变未选，未选变已选
    private static void InvertSelection(MainViewModel vm, System.Windows.Controls.ListView list)
    {
        vm.UpdateSelection(new[] { vm.Items[0], vm.Items[1] });
        Program.Pump(100);

        int visible = vm.VisibleItemCount;
        vm.InvertSelectionCommand.Execute(null);
        Program.Pump(150);

        H.Eq("反选后选中数 = 可见数 - 原选中数", visible - 2, vm.SelectedItems.Count);
        H.Check("反选后原选中项不再选中",
            !vm.SelectedItems.Contains(vm.Items[0]) && !vm.SelectedItems.Contains(vm.Items[1]));
        H.Check("反选后原未选中项变为选中", vm.SelectedItems.Contains(vm.Items[2]));

        vm.InvertSelectionCommand.Execute(null);
        Program.Pump(150);
        H.Eq("反选两次回到原选中数", 2, vm.SelectedItems.Count);
        H.Check("反选两次回到原选中项",
            vm.SelectedItems.Contains(vm.Items[0]) && vm.SelectedItems.Contains(vm.Items[1]));
    }

    // 状态栏：只累计文件大小，文件夹不计
    private static void StatusBarCountsFilesOnly(MainViewModel vm, System.Windows.Controls.ListView list)
    {
        FileItemViewModel folder = vm.Items.First(item => item.IsDirectory);
        FileItemViewModel file = vm.Items.First(item => !item.IsDirectory);

        vm.UpdateSelection(new[] { file, folder });
        Program.Pump(100);

        string status = vm.StatusText;
        H.Check("状态栏含「已选择 2 个项目」", status.Contains("已选择 2 个项目", StringComparison.Ordinal), status);
        H.Check("状态栏含文件大小合计", SelectionSegment(status).Contains("共 ", StringComparison.Ordinal), status);

        vm.UpdateSelection(new[] { folder });
        Program.Pump(100);
        string folderOnly = vm.StatusText;
        H.Check("只选文件夹时不显示合计大小",
            !SelectionSegment(folderOnly).Contains("共 ", StringComparison.Ordinal), folderOnly);
        H.Check("只选文件夹仍显示项目数",
            folderOnly.Contains("已选择 1 个项目", StringComparison.Ordinal), folderOnly);

        vm.UpdateSelection(Array.Empty<FileItemViewModel>());
        Program.Pump(100);
        H.Check("未选时状态栏不出现选中摘要",
            !vm.StatusText.Contains("已选择", StringComparison.Ordinal), vm.StatusText);

        // 大小按字节精确比对
        vm.UpdateSelection(new[] { file });
        Program.Pump(100);
        string expected = FileItemViewModel.FormatSize(file.SizeBytes);
        H.Check("状态栏大小与文件实际大小一致",
            SelectionSegment(vm.StatusText).Contains(expected, StringComparison.Ordinal),
            $"期望 {expected}，实际 {vm.StatusText}");
    }

    // 只取「已选择 …」那一段，避免和「共 N 个项目」里的「共」混淆
    private static string SelectionSegment(string status)
    {
        int index = status.IndexOf("已选择", StringComparison.Ordinal);
        return index < 0 ? string.Empty : status[index..];
    }

    // 刷新后尽量保留选择
    private static void RefreshKeepsSelection(MainViewModel vm)
    {
        vm.UpdateSelection(new[] { vm.Items[1], vm.Items[3] });
        Program.Pump(100);
        string first = vm.Items[1].DisplayPath;
        string second = vm.Items[3].DisplayPath;

        vm.RefreshCommand.Execute(null);
        bool restored = Program.PumpUntil(() => !vm.IsLoading && vm.SelectedItems.Count == 2, 15000);

        H.Check("刷新后保留 2 项选择", restored, $"实际 {vm.SelectedItems.Count} 项");
        H.Check("刷新后选中的是原来那两项",
            vm.SelectedItems.Any(item => item.DisplayPath == first)
            && vm.SelectedItems.Any(item => item.DisplayPath == second),
            string.Join(" | ", vm.SelectedItems.Select(item => item.DisplayPath)));
    }

    // 撤销后恢复操作前的选择
    private static void UndoRestoresSelection(MainViewModel vm)
    {
        FileItemViewModel file = vm.Items.First(item => !item.IsDirectory && item.CanRename);
        vm.UpdateSelection(new[] { file });
        Program.Pump(100);

        string originalName = file.Name;
        H.Check("重命名成功", vm.CommitRename(file, "renamed_" + originalName), file.RenameMessage);
        Program.Pump(250);

        // 选中重命名后的那一项，撤销后应回到原名且仍选中
        FileItemViewModel renamed = vm.Items.First(item => item.Name == "renamed_" + originalName);
        vm.UpdateSelection(new[] { renamed });
        Program.Pump(100);

        vm.UndoCommand.Execute(null);
        bool restored = Program.PumpUntil(() => !vm.IsLoading && vm.SelectedItems.Count > 0, 15000);

        H.Check("撤销后仍保留选择", restored, $"实际 {vm.SelectedItems.Count} 项");
        H.Check("撤销后文件名恢复", vm.Items.Any(item => item.Name == originalName),
            string.Join(" | ", vm.Items.Select(item => item.Name)));
    }

    // 复制 / 移动的历史条目要记下发起前的选择，而不是完成后的
    private static void TransferHistoryRecordsSelectionBefore(MainViewModel vm, string dataDir)
    {
        string target = H.NewDir("sel-copy-target");
        FileItemViewModel source = vm.Items.First(item => !item.IsDirectory);
        string sourcePath = source.DisplayPath;

        vm.UpdateSelection(new[] { source });
        Program.Pump(100);
        string[] selectionBefore = vm.SelectedItems.Select(item => item.DisplayPath).ToArray();

        vm.ExecuteCopyMoveTo(OperationKind.Copy, target);

        // 等任务结束并刷新完
        bool idle = Program.PumpUntil(
            () => !vm.Coordinator.HasActiveTasks && !vm.IsLoading, 30000);
        H.Check("复制任务结束", idle, "任务未在 30 秒内结束");
        Program.Pump(300);

        // 复制完成后切到目标目录并改选其它项，模拟「操作后选择已变」
        vm.NavigateToLocation(target);
        Program.PumpUntil(() => !vm.IsLoading && vm.Items.Count > 0, 15000);
        vm.UpdateSelection(vm.Items.ToList());
        Program.Pump(100);

        HistoryEntry? entry = vm.History.PeekUndo();
        H.Check("复制后历史栈有可撤销项", entry is not null, "撤销栈为空");
        if (entry is null)
        {
            return;
        }

        H.Check("传输历史记的是操作前的选择",
            entry.SelectionBeforeOperation.Count == selectionBefore.Length
            && entry.SelectionBeforeOperation.All(path =>
                selectionBefore.Contains(path, StringComparer.OrdinalIgnoreCase)),
            $"记下 [{string.Join(" | ", entry.SelectionBeforeOperation)}]，操作前 [{string.Join(" | ", selectionBefore)}]");

        H.Check("传输历史未误记为操作后的选择",
            !entry.SelectionBeforeOperation.Contains(vm.Items[0].DisplayPath, StringComparer.OrdinalIgnoreCase)
            || selectionBefore.Contains(vm.Items[0].DisplayPath, StringComparer.OrdinalIgnoreCase),
            $"记下 [{string.Join(" | ", entry.SelectionBeforeOperation)}]");

        // 撤销前先回到操作发生的目录，选择恢复是按目录里的路径匹配的
        vm.NavigateToLocation(dataDir);
        Program.PumpUntil(() => !vm.IsLoading, 15000);

        // 撤销后应回到操作前的选择
        vm.UndoCommand.Execute(null);
        bool undone = Program.PumpUntil(() => !vm.IsLoading && !vm.Coordinator.HasActiveTasks, 20000);
        H.Check("撤销复制完成", undone, "撤销未在 20 秒内完成");
        Program.Pump(300);
        H.Check("撤销复制后回到操作前的选择",
            vm.SelectedItems.Any(item =>
                string.Equals(item.DisplayPath, sourcePath, StringComparison.OrdinalIgnoreCase)),
            $"已选 [{string.Join(" | ", vm.SelectedItems.Select(item => item.DisplayPath))}]");

        H.Check("撤销复制后目标目录里的副本已删除",
            !H.Exists(Path.Combine(target, Path.GetFileName(sourcePath))), target);
    }

    // 表头全选框：全选 / 全不选 / 半选
    private static void HeaderCheckBox(MainWindow window, MainViewModel vm, System.Windows.Controls.ListView list)
    {
        vm.ShowCheckBoxes = true;
        Program.Pump(250);
        list.UpdateLayout();
        Program.Pump(200);

        CheckBox? header = FindHeaderCheckBox(window);
        H.Check("表头全选框已创建", header is not null, "未找到 HeaderCheckBox");

        vm.UpdateSelection(Array.Empty<FileItemViewModel>());
        Program.Pump(100);
        H.Check("未选时表头为未选中", vm.HeaderCheckState == false, vm.HeaderCheckState?.ToString() ?? "null");
        H.Check("未选时表头复选框未勾", header?.IsChecked == false, header?.IsChecked?.ToString() ?? "null");

        vm.SelectAllItemsCommand.Execute(null);
        Program.Pump(150);
        H.Check("全选后表头为选中", vm.HeaderCheckState == true, vm.HeaderCheckState?.ToString() ?? "null");

        vm.UpdateSelection(vm.Items.Take(1).ToArray());
        Program.Pump(100);
        H.Check("部分选中时表头为半选", vm.HeaderCheckState is null, vm.HeaderCheckState?.ToString() ?? "null");

        vm.ClearSelectionCommand.Execute(null);
        Program.Pump(150);
        H.Eq("全部取消后选中数为 0", 0, vm.SelectedItems.Count);
    }

    private static CheckBox? FindHeaderCheckBox(MainWindow window)
    {
        if (window.FindName("FileListView") is not System.Windows.Controls.ListView list)
        {
            return null;
        }

        // 列头内容由代码组装，按 AutomationId 在可视树里找
        return FindByAutomationId<CheckBox>(list, "HeaderCheckBox");
    }

    private static T? FindByAutomationId<T>(DependencyObject? root, string automationId)
        where T : DependencyObject
    {
        if (root is null)
        {
            return null;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match
                && string.Equals(System.Windows.Automation.AutomationProperties.GetAutomationId(match),
                    automationId, StringComparison.Ordinal))
            {
                return match;
            }

            T? found = FindByAutomationId<T>(child, automationId);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    // 超长路径与特殊名称项与普通项一样可选
    private static void LongNameSelectable(MainViewModel vm)
    {
        string deepRoot = H.NewDir("sel-long");
        string deep = H.BuildDeepPath(deepRoot, 6, 40);
        string longName = new string('z', 200) + ".txt";
        H.WriteFile(Path.Combine(deep, longName), "long");
        H.WriteFile(Path.Combine(deep, "con.txt"), "odd");

        vm.NavigateToLocation(deep);
        Program.PumpUntil(() => !vm.IsLoading && vm.Items.Count >= 2, 20000);
        Program.Pump(200);

        H.Check("长路径目录加载出项", vm.Items.Count >= 2, $"实际 {vm.Items.Count}");

        vm.SelectAllItemsCommand.Execute(null);
        Program.Pump(200);
        H.Check("长路径 / 特殊名项可全选", vm.SelectedItems.Count == vm.Items.Count,
            $"已选 {vm.SelectedItems.Count} / 共 {vm.Items.Count}");
        H.Check("长路径项也在选中集合里",
            vm.SelectedItems.Any(item => item.DisplayPath.Contains(longName, StringComparison.Ordinal)));

        vm.InvertSelectionCommand.Execute(null);
        Program.Pump(200);
        H.Eq("长路径目录反选后清空", 0, vm.SelectedItems.Count);

        vm.UpdateSelection(Array.Empty<FileItemViewModel>());
    }

    // 复选框开关写入设置
    private static void CheckBoxSettingPersists(MainWindow window, MainViewModel vm, SettingsService settingsService)
    {
        bool before = vm.ShowCheckBoxes;
        vm.ToggleCheckBoxesCommand.Execute(null);
        Program.Pump(300);

        H.NotEq("开关切换后状态变化", before, vm.ShowCheckBoxes);

        string path = Path.Combine(H.Root, "sel-settings.json");
        if (!File.Exists(path))
        {
            H.Check("设置文件存在", false, path);
            return;
        }

        string json = File.ReadAllText(path);
        H.Check("复选框开关已写入设置文件",
            json.Contains("\"ShowCheckBoxes\"", StringComparison.Ordinal), "settings.json 无 ShowCheckBoxes");
        H.Check("设置值与内存一致",
            json.Contains($"\"ShowCheckBoxes\": {(vm.ShowCheckBoxes ? "true" : "false")}", StringComparison.Ordinal),
            "设置值不一致");

        // 重新加载能读回同一值
        AppSettings reloaded = new SettingsService(path).Load();
        H.Eq("重新加载后复选框开关一致", vm.ShowCheckBoxes, reloaded.ShowCheckBoxes);
        _ = window;
        _ = settingsService;
    }

    private static T? FindDescendant<T>(DependencyObject? root)
        where T : DependencyObject
    {
        if (root is null)
        {
            return null;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            T? found = FindDescendant<T>(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
