using System.IO;
using ChangJing.App.Controls;
using ChangJing.App.Core.DragDrop;
using ChangJing.App.Core.FileSystem;
using ChangJing.App.Core.Grouping;
using ChangJing.App.Core.LongPath;
using ChangJing.App.Core.Operations;
using ChangJing.App.Core.PathRepair;
using ChangJing.App.Core.RecycleBin;
using ChangJing.App.Core.Settings;
using ChangJing.App.Services;
using ChangJing.App.Services.Preview;
using ChangJing.App.Services.Search;
using ChangJing.App.ViewModels;

namespace ChangJing.Regression;

/// <summary>核心层回归：长路径、名称校验、拖放、分组、搜索、文件操作、冲突</summary>
public static class CoreSuite
{
    public static void Run()
    {
        H.Group("A1 长路径与特殊名称（浏览 / 导航 / 面包屑）");
        LongPathAndBreadcrumbs();

        H.Group("A2 名称校验 / 新建 / 重命名");
        NameValidation();

        H.Group("A3 复制 / 移动 / 重命名 / 删除（含长路径 > 300、保留名、尾随点）");
        FileOperationBasics();

        H.Group("A4 回收站（移入 / 还原 / 撤销 / 重做 / 冲突）");
        RecycleBinFlow();

        H.Group("A5 冲突处理 / 跨卷 ForceCrossVolume");
        ConflictAndCrossVolume();

        H.Group("A6 拖放效果计算 + 背景菜单命中规则（纯逻辑）");
        DropAndHitRules();

        H.Group("A7 分组规则 + 递归搜索 + 暂停");
        GroupingAndSearch();

        H.Group("A8 修复路径规则 + 预览三类分类");
        PathRepairAndPreview();

        H.Group("A9 标题栏文字格式（纯逻辑）");
        TitleTextFormatRules();

        H.Group("A10 启动命令行位置参数 + 运行中互斥体（纯逻辑）");
        CommandLineStartupRules();
        RunningMutexMechanism();

        H.Group("A11 x86 WOW64 重定向作用域（浏览 System32 后 Shell 仍正常）");
        Wow64ScopedRedirection();
    }


    /// <summary>启动命令行解析：文件夹 / 盘符 / 文件 / 不存在 / \\?\ 前缀 / 长路径 / UNC</summary>
    private static void CommandLineStartupRules()
    {
        string root = H.NewDir("cli-root");
        string sub = H.NewDir(Path.Combine(root, "sub"));
        string file = Path.Combine(root, "note.txt");
        H.WriteFile(file, "note");

        // 文件夹 → Folder，显示路径正确
        StartupTarget folder = CommandLineStartup.Resolve(new[] { sub });
        H.Eq("文件夹参数 → Folder", StartupTargetKind.Folder, folder.Kind);
        H.EqPath("文件夹参数显示路径", sub, folder.FolderPath);
        H.Check("文件夹参数无选中项", folder.SelectItemPath is null);
        H.Check("文件夹参数无提示", folder.Message is null);

        // 盘符 → 根 C:\
        string driveLetter = Path.GetPathRoot(root)!.TrimEnd('\\');
        StartupTarget drive = CommandLineStartup.Resolve(new[] { driveLetter });
        H.Eq("盘符参数 → Folder", StartupTargetKind.Folder, drive.Kind);
        H.EqPath("盘符参数解析为根", Path.GetPathRoot(root)!, drive.FolderPath);

        // 右键盘 / 磁盘根目录右键：注册表 "C:\" 里的 \" 被命令行当成转义，程序实际收到 C:"
        string driveRoot = Path.GetPathRoot(root)!;
        string mangledDriveRoot = driveRoot.Substring(0, driveRoot.Length - 1) + "\"";
        H.Eq("转义盘符 C:\\\" → Folder", StartupTargetKind.Folder, CommandLineStartup.Resolve(new[] { mangledDriveRoot }).Kind);
        H.EqPath("转义盘符 C:\\\" 还原为根", driveRoot, CommandLineStartup.Resolve(new[] { mangledDriveRoot }).FolderPath);

        // 真实 Windows 命令行解析：注册表命令 "<exe>" "C:\" 传进来的实际参数
        string receivedDriveRoot = ParseFirstArgumentWin32("\"changjing.exe\" \"C:\\\"");
        H.Eq("Win32 解析 \"C:\\\" 实际收到 C:\"", driveRoot.Substring(0, driveRoot.Length - 1) + "\"", receivedDriveRoot);
        H.Eq("Win32 解析后的 C:\" 能还原为根", StartupTargetKind.Folder, CommandLineStartup.Resolve(new[] { receivedDriveRoot }).Kind);
        H.EqPath("Win32 解析后的 C:\" 路径为根", driveRoot, CommandLineStartup.Resolve(new[] { receivedDriveRoot }).FolderPath);

        // 字面量成对引号形态 "\"C:\\\""（首字符是引号）交给既有引号处理
        H.Eq("\"C:\\\" 成对引号 → Folder", StartupTargetKind.Folder, CommandLineStartup.Resolve(new[] { "\"C:\\\"" }).Kind);
        H.EqPath("\"C:\\\" 成对引号路径为根", driveRoot, CommandLineStartup.Resolve(new[] { "\"C:\\\"" }).FolderPath);

        // 带空格的文件夹：正常尾 \ 与「尾 \ 被转义吃掉」两种形态都应解析到同一路径
        string spaced = H.NewDir(Path.Combine(root, "a b c"));
        StartupTarget spacedNormal = CommandLineStartup.Resolve(new[] { spaced + "\\" });
        H.Eq("带空格 + 尾 \\ → Folder", StartupTargetKind.Folder, spacedNormal.Kind);
        H.EqPath("带空格 + 尾 \\ 路径正确", spaced, spacedNormal.FolderPath);

        string spacedMangled = spaced + "\"";   // 等价于收到 "C:\...a b c\"
        StartupTarget spacedFixed = CommandLineStartup.Resolve(new[] { spacedMangled });
        H.Eq("带空格 + 尾 \\ 被转义 → Folder", StartupTargetKind.Folder, spacedFixed.Kind);
        H.EqPath("带空格 + 尾 \\ 被转义路径还原", spaced, spacedFixed.FolderPath);

        // 无空格文件夹：尾 \ 被转义吃掉
        StartupTarget subMangled = CommandLineStartup.Resolve(new[] { sub + "\"" });
        H.Eq("尾 \\ 被转义 → Folder", StartupTargetKind.Folder, subMangled.Kind);
        H.EqPath("尾 \\ 被转义路径还原", sub, subMangled.FolderPath);

        // 尾部带 \ 的文件夹（未被转义，正常）
        StartupTarget subTrailingSlash = CommandLineStartup.Resolve(new[] { sub + "\\" });
        H.Eq("尾部带 \\ → Folder", StartupTargetKind.Folder, subTrailingSlash.Kind);
        H.EqPath("尾部带 \\ 路径正确", sub, subTrailingSlash.FolderPath);

        // 文件 → File，父目录 + 选中文件
        StartupTarget fileTarget = CommandLineStartup.Resolve(new[] { file });
        H.Eq("文件参数 → File", StartupTargetKind.File, fileTarget.Kind);
        H.EqPath("文件参数父目录", root, fileTarget.FolderPath);
        H.EqPath("文件参数选中路径", file, fileTarget.SelectItemPath!);

        // 带引号 + 结尾反斜杠
        StartupTarget quoted = CommandLineStartup.Resolve(new[] { "\"" + root + "\\\"" });
        H.Eq("引号 + 结尾反斜杠 → Folder", StartupTargetKind.Folder, quoted.Kind);
        H.EqPath("引号 + 结尾反斜杠显示路径", root, quoted.FolderPath);

        // 不存在 → Default + 中文提示
        string missing = Path.Combine(root, "no-such-dir-" + Guid.NewGuid().ToString("N")[..6]);
        StartupTarget notFound = CommandLineStartup.Resolve(new[] { missing });
        H.Eq("不存在路径 → Default", StartupTargetKind.Default, notFound.Kind);
        H.Check("不存在路径有非空中文提示", !string.IsNullOrEmpty(notFound.Message), notFound.Message ?? "null");
        H.Check("不存在路径提示含中文与原名",
            notFound.Message!.Contains("找不到", StringComparison.Ordinal)
            && notFound.Message.Contains(Path.GetFileName(missing), StringComparison.Ordinal), notFound.Message);

        // \\?\ 前缀 → 显示去前缀
        StartupTarget extended = CommandLineStartup.Resolve(new[] { @"\\?\C:\Windows" });
        H.Eq(@"\\?\C:\Windows → Folder", StartupTargetKind.Folder, extended.Kind);
        H.Check(@"\\?\C:\Windows 显示不含 \\?\ ", !extended.FolderPath.Contains(@"\\?", StringComparison.Ordinal), extended.FolderPath);
        H.EqPath(@"\\?\C:\Windows 显示为 C:\Windows", @"C:\Windows", extended.FolderPath);

        // 长 / 深路径
        string deep = H.BuildDeepPath(H.NewDir("cli-deep"), 8, 45);
        StartupTarget deepTarget = CommandLineStartup.Resolve(new[] { deep });
        H.Eq("长路径 → Folder", StartupTargetKind.Folder, deepTarget.Kind);
        H.EqPath("长路径显示路径", deep, deepTarget.FolderPath);
        H.Check("长路径显示不含 \\\\?\\", !deepTarget.FolderPath.Contains(@"\\?", StringComparison.Ordinal));

        // --folder 选项被跳过，其后位置参数生效
        StartupTarget withOption = CommandLineStartup.Resolve(new[] { "--folder", "handoff.json", sub });
        H.Eq("跳过 --folder 后取位置参数", StartupTargetKind.Folder, withOption.Kind);
        H.EqPath("跳过 --folder 后路径正确", sub, withOption.FolderPath);

        // 无参数 → Default
        H.Eq("无参数 → Default", StartupTargetKind.Default, CommandLineStartup.Resolve(Array.Empty<string>()).Kind);
    }


    /// <summary>运行中互斥体机制：同名互斥体创建后可从本进程 TryOpenExisting</summary>
    private static void RunningMutexMechanism()
    {
        const string name = "ChangJing.App.Running";

        using var mutex = new System.Threading.Mutex(initiallyOwned: false, name);
        H.Check("互斥体创建成功", mutex is not null);
        H.Check("运行中互斥体 TryOpenExisting 为 true",
            System.Threading.Mutex.TryOpenExisting(name, out System.Threading.Mutex? opened), "未检测到已存在的互斥体");
        opened?.Dispose();

        // 与 App 常量一致
        H.Eq("互斥体名与 App 常量一致", name, ChangJing.App.App.RunningMutexName);
    }


    /// <summary>x86 下 WOW64 重定向按作用域开关：System32 读到真实目录，作用域外重定向必须还原，Shell / COM 仍正常</summary>
    // x86 进程默认看不到 System32 里的 64 位专属文件，靠 NativeMethods 包装的作用域穿透；
    // 关键是不能把整线程重定向永久关闭，否则同线程 LoadLibrary / COM 会去 System32 找 32 位 DLL 而随机出错
    private static void Wow64ScopedRedirection()
    {
        bool applicable = Wow64FileSystemRedirection.IsApplicable;
        H.Info($"  Is64BitProcess={Environment.Is64BitProcess}，IsApplicable={applicable}");

        // wow64win.dll 只存在于真实的 64 位 System32，是被重定向时的判定标志
        const string markerName = "wow64win.dll";
        const string system32 = @"C:\Windows\System32";

        // 1) 通过产品枚举器浏览 System32：x86 下应能穿透重定向看到标记文件
        var enumerator = new DirectoryEnumerator(batchSize: 2000);
        var (items, result) = enumerator.ToList(PathHelper.ToLong(system32));
        H.Check("枚举 System32 成功", result.Success, result.Error);
        H.Check("枚举 System32 有内容", items.Count > 0, $"{items.Count} 项");
        H.Check(
            "System32 枚举到 wow64win.dll（穿透重定向）",
            items.Any(item => string.Equals(item.Name, markerName, StringComparison.OrdinalIgnoreCase)));

        // FileOperations 属性查询同样应穿透
        FileEntry? kernel32 = FileOperations.TryCreateEntry(PathHelper.ToLong(Path.Combine(system32, "kernel32.dll")));
        H.Check("属性查询命中 System32\\kernel32.dll", kernel32 is not null);
        H.Check("kernel32.dll 非目录", kernel32 is { IsDirectory: false });

        // 2) 决定性断言：枚举结束后当前线程的重定向必须已还原（旧「整线程永久关闭」写法会在 x86 上失败）
        int afterEnumeration = ProbeMarkerVisible();
        if (applicable)
        {
            H.Eq("x86：枚举结束后线程重定向已还原（标记不可见）", 0, afterEnumeration);
        }
        else
        {
            H.Eq("x64：无重定向，标记始终可见", 1, afterEnumeration);
        }

        // 3) 作用域内确实穿透，且离开作用域后还原
        if (applicable)
        {
            int inside;
            using (Wow64FileSystemRedirection.Suspend())
            {
                inside = ProbeMarkerVisible();
            }

            H.Eq("作用域内：System32 标记可见", 1, inside);
            H.Eq("作用域结束：重定向状态还原", 0, ProbeMarkerVisible());

            // 作用域内自嵌套也不破坏状态
            using (Wow64FileSystemRedirection.Suspend())
            using (Wow64FileSystemRedirection.Suspend())
            {
                H.Eq("嵌套作用域内：System32 标记可见", 1, ProbeMarkerVisible());
            }

            H.Eq("嵌套作用域结束：重定向状态还原", 0, ProbeMarkerVisible());
        }

        // 4) 同一流程里继续取图标 / 调 Shell 接口，确认未被破坏（作用域外不关闭重定向）
        var folderIcon = SystemIconService.GetIcon("folder", isDirectory: true);
        H.Check("浏览 System32 后仍能取系统文件夹图标", folderIcon is not null);
        var exeIcon = SystemIconService.GetIconForSize("changjing.exe", isDirectory: false, 48);
        H.Check("浏览 System32 后仍能取 48px 图标（走 Shell 图像列表）", exeIcon is not null);
        var shield = SystemIconService.GetShieldIcon(16);
        H.Check("浏览 System32 后仍能取系统盾牌图标", shield is not null);
        string typeName = SystemIconService.GetTypeName("changjing.exe", isDirectory: false);
        H.Check("浏览 System32 后仍能取类型名（SHGetFileInfo）", !string.IsNullOrEmpty(typeName), typeName);
    }

    // 裸探针：直接 Win32 读属性，不经产品 NativeMethods 包装，反映当前线程真实重定向状态
    // 1 = 看到真实 System32（未被重定向），0 = 被重定向
    private static int ProbeMarkerVisible()
    {
        try
        {
            uint attributes = GetFileAttributesProbe(PathHelper.ToLong(@"C:\Windows\System32\wow64win.dll"));
            return attributes == 0xFFFF_FFFF ? 0 : 1;
        }
        catch
        {
            return -1;
        }
    }

    [System.Runtime.InteropServices.DllImport(
        "kernel32.dll",
        EntryPoint = "GetFileAttributesW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true)]
    private static extern uint GetFileAttributesProbe(string lpFileName);

    // 用 Windows 自己的命令行解析规则取「程序名之后的第一个参数」，复现注册表命令 "<exe>" "%1" 的真实效果
    private static string ParseFirstArgumentWin32(string commandLine)
    {
        IntPtr argv = CommandLineToArgvW(commandLine, out int argc);
        try
        {
            if (argv == IntPtr.Zero || argc < 2)
            {
                return string.Empty;
            }

            IntPtr first = System.Runtime.InteropServices.Marshal.ReadIntPtr(argv, System.IntPtr.Size);
            return System.Runtime.InteropServices.Marshal.PtrToStringUni(first) ?? string.Empty;
        }
        finally
        {
            if (argv != IntPtr.Zero)
            {
                LocalFree(argv);
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string lpCmdLine,
        out int pNumArgs);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);


    /// <summary>标题栏纯逻辑：应用名在前、无位置时只有「畅径」、盘符根判定</summary>
    private static void TitleTextFormatRules()
    {
        H.Eq("无位置 + 非管理员 = 畅径", "畅径", TitleTextFormat.Compose(null, elevated: false));
        H.Eq("无位置 + 管理员 = 畅径（管理员）", "畅径（管理员）", TitleTextFormat.Compose(null, elevated: true));
        H.Eq("空串视为无位置", "畅径", TitleTextFormat.Compose(string.Empty, elevated: false));
        H.Eq("普通文件夹 + 非管理员", "畅径 - 文档", TitleTextFormat.Compose("文档", elevated: false));
        H.Eq("普通文件夹 + 管理员", "畅径（管理员） - 文档", TitleTextFormat.Compose("文档", elevated: true));
        H.Eq("此电脑", "畅径 - 此电脑", TitleTextFormat.Compose("此电脑", elevated: false));
        H.Eq("回收站", "畅径 - 回收站", TitleTextFormat.Compose("回收站", elevated: false));
        H.Eq("搜索结果位置名", "搜索“报告”", TitleTextFormat.SearchLocationName("  报告  "));

        H.Eq("应用名标签（非管理员）", "畅径", TitleTextFormat.Brand(elevated: false));
        H.Eq("应用名标签（管理员）", "畅径（管理员）", TitleTextFormat.Brand(elevated: true));

        (string main, string suffix) = TitleTextFormat.Split("文档", elevated: false);
        H.Eq("主文字 = 应用名标签「畅径」", "畅径", main);
        H.Eq("后缀 = 「 - 位置名」", " - 文档", suffix);

        (main, suffix) = TitleTextFormat.Split(null, elevated: true);
        H.Eq("无位置时主文字 = 畅径（管理员）", "畅径（管理员）", main);
        H.Eq("无位置时无灰色后缀", string.Empty, suffix);

        H.Check("C:\\ 是盘符根", TitleTextFormat.IsDriveRoot(@"C:\"));
        H.Check("D:\\ 是盘符根", TitleTextFormat.IsDriveRoot(@"d:\"));
        H.Check("C:\\Users 不是盘符根", !TitleTextFormat.IsDriveRoot(@"C:\Users"));
        H.Check("null 不是盘符根", !TitleTextFormat.IsDriveRoot(null));
    }


    private static void LongPathAndBreadcrumbs()
    {
        string root = H.NewDir("longpath");
        string deep = H.BuildDeepPath(root, 8, 45);
        H.Check("深层目录可创建（>300 字符）", deep.Length > 300, $"长度 {deep.Length}");

        string deepFile = Path.Combine(deep, "深层文件.txt");
        H.WriteFile(deepFile, "hello");
        H.Check("长路径文件可写可读", H.ReadFile(deepFile) == "hello");

        string longPath = PathHelper.ToLong(deepFile);
        H.Check(@"ToLong 前缀为 \\?\ ", longPath.StartsWith(@"\\?\", StringComparison.Ordinal));
        H.Check(@"ToDisplay 去除 \\?\ ", !PathHelper.ToDisplay(longPath).StartsWith(@"\\?\", StringComparison.Ordinal));

        // 面包屑
        IReadOnlyList<BreadcrumbSegment> segs = Breadcrumbs.Build(deep, "此电脑", "::ThisPC", "回收站", "::RecycleBin");
        H.Check("面包屑段数 > 3", segs.Count > 3, $"实际 {segs.Count}");
        H.Check("面包屑首段为「此电脑」", segs[0].IsThisPc);
        H.Check(@"面包屑不含 \\?\ ", segs.All(s => !s.DisplayName.Contains(@"\\?\")));

        IReadOnlyList<BreadcrumbSegment> recycle = Breadcrumbs.Build("::RecycleBin", "此电脑", "::ThisPC", "回收站", "::RecycleBin");
        H.Check("回收站面包屑只有一段", recycle.Count == 1 && recycle[0].DisplayName == "回收站");

        // 保留名 / 尾随点 / 超长判定
        H.Check("保留名 con.txt 被识别", LongPathRules.IsReservedName("con.txt"));
        H.Check("尾随点被识别", LongPathRules.HasTrailingDotOrSpace("name."));
        H.Check("尾随空格被识别", LongPathRules.HasTrailingDotOrSpace("name "));
        H.Check("普通名不误报", !LongPathRules.IsReservedName("console.txt"));

        // 用户输入解析
        H.Check("解析绝对路径成功", PathHelper.TryParseUserInput(deep, out PathParseResult p) && p.Success);
        H.Check("解析非法路径失败", !PathHelper.TryParseUserInput("", out _));
    }


    private static void NameValidation()
    {
        string dir = H.NewDir("names");
        H.Check("空名被拒", NameValidator.Validate("", dir).HasError);
        H.Check("非法字符被拒", NameValidator.Validate("a<b", dir).HasError);
        H.Check("保留名被拒", NameValidator.Validate("CON", dir).HasError);
        H.Check("尾随点被拒", NameValidator.Validate("abc.", dir).HasError);
        H.Check("普通名通过", NameValidator.Validate("正常名称.txt", dir).IsValid);

        // 新建文件夹
        FileOperationResult created = FileOperations.CreateDirectory(dir);
        H.Check("新建文件夹成功", created.Success && Directory.Exists(PathHelper.ToLong(created.DisplayPath)));

        // 重命名
        string src = Path.Combine(dir, "rename-src.txt");
        H.WriteFile(src, "x");
        FileOperationResult renamed = FileOperations.Rename(PathHelper.ToLong(src), "rename-dst.txt");
        H.Check("重命名成功", renamed.Success && !H.Exists(src) && H.Exists(Path.Combine(dir, "rename-dst.txt")));
    }


    private static void FileOperationBasics()
    {
        string root = H.NewDir("ops");
        string srcDir = Path.Combine(root, "src");
        string dstDir = Path.Combine(root, "dst");
        H.MakeDir(srcDir);
        H.MakeDir(dstDir);

        // 普通文件复制 / 移动
        string a = Path.Combine(srcDir, "a.txt");
        H.WriteFile(a, "AAA");
        FileTransferResult copy = new FileTransferService().Copy(new[] { a }, dstDir);
        H.Check("复制已受理", copy.Started, copy.Message);
        bool copied = PumpOpsUntil(() => H.Exists(Path.Combine(dstDir, "a.txt")));
        H.Check("复制落地且源保留", copied && H.Exists(a) && H.ReadFile(Path.Combine(dstDir, "a.txt")) == "AAA");

        string b = Path.Combine(srcDir, "b.txt");
        H.WriteFile(b, "BBB");
        FileTransferResult move = new FileTransferService().Move(new[] { b }, dstDir);
        H.Check("移动已受理", move.Started, move.Message);
        bool moved = PumpOpsUntil(() => H.Exists(Path.Combine(dstDir, "b.txt")) && !H.Exists(b));
        H.Check("移动落地且源删除", moved);

        // 长路径 > 300 文件复制 / 移动 / 重命名 / 删除
        string deep = H.BuildDeepPath(Path.Combine(root, "deep"), 8, 45);
        string longFile = Path.Combine(deep, "long.txt");
        H.WriteFile(longFile, "LONG");
        H.Check("长路径文件长度 > 300", longFile.Length > 300, $"{longFile.Length}");

        string dst2 = Path.Combine(root, "dst2");
        H.MakeDir(dst2);
        FileTransferResult lc = new FileTransferService().Copy(new[] { longFile }, dst2);
        H.Check("长路径复制已受理", lc.Started, lc.Message);
        bool lcOk = PumpOpsUntil(() => H.Exists(Path.Combine(dst2, "long.txt")));
        H.Check("长路径复制落地", lcOk && H.ReadFile(Path.Combine(dst2, "long.txt")) == "LONG");

        FileOperationResult lr = FileOperations.Rename(PathHelper.ToLong(Path.Combine(dst2, "long.txt")), "long2.txt");
        H.Check("长路径重命名成功", lr.Success, lr.Error);

        // 保留名 + 尾随点文件：创建 / 复制 / 删除必须用 \\?\
        string reserved = Path.Combine(srcDir, "con.txt");
        string trailing = Path.Combine(srcDir, "trail.");
        H.WriteFile(reserved, "R");
        H.WriteFile(trailing, "T");
        H.Check("保留名文件创建成功", H.Exists(reserved));
        H.Check("尾随点文件创建成功", H.Exists(trailing));

        string dst3 = Path.Combine(root, "dst3");
        H.MakeDir(dst3);
        new FileTransferService().Copy(new[] { reserved, trailing }, dst3);
        bool specialOk = PumpOpsUntil(() => H.Exists(Path.Combine(dst3, "con.txt")) && H.Exists(Path.Combine(dst3, "trail.")));
        H.Check("保留名 / 尾随点复制落地", specialOk);

        // 删除走永久删除引擎，迭代
        DeleteTreeResult del = DeleteEngine.DeleteTree(PathHelper.ToLong(Path.Combine(dst3, "trail.")), false, "trail.", CancellationToken.None);
        H.Check("尾随点文件永久删除成功", del.Success);
        H.Check("尾随点文件已不存在", !H.Exists(Path.Combine(dst3, "trail.")));

        // 深层目录永久删除不爆栈
        DeleteTreeResult deepDel = DeleteEngine.DeleteTree(PathHelper.ToLong(Path.Combine(root, "deep")), true, "deep", CancellationToken.None);
        H.Check("深层目录树永久删除成功", deepDel.Success);
    }


    private static void RecycleBinFlow()
    {
        string root = H.NewDir("recycle");
        string testRoot = Path.Combine(root, "root");
        H.MakeDir(testRoot);

        // 回收站根注入测试目录，绝不碰真实卷根
        var options = new RecycleBinOptions
        {
            RootOverride = _ => Path.Combine(testRoot, "$recycle"),
            VolumeRootsOverride = () => new[] { testRoot },
        };
        var store = new RecycleBinStore(options);

        string victimDir = Path.Combine(root, "victim");
        H.MakeDir(victimDir);
        string f1 = Path.Combine(victimDir, "one.txt");
        string f2 = Path.Combine(victimDir, "two.txt");
        H.WriteFile(f1, "111");
        H.WriteFile(f2, "222");
        DateTime before = File.GetLastWriteTimeUtc(PathHelper.ToLong(f1));

        var m1 = store.MoveIn(f1);
        H.Check("文件移入回收站成功", m1.Success, m1.Error);
        H.Check("源文件已消失", !H.Exists(f1));
        H.Check("条目元数据齐全", m1.Entry is { } e1
            && e1.OriginalPath == f1 && !e1.IsDirectory && e1.SizeBytes == 3);

        var m2 = store.MoveIn(f2);
        H.Check("第二个文件移入成功", m2.Success, m2.Error);

        IReadOnlyList<RecycleBinEntry> entries = store.ListEntries(testRoot);
        H.Check("回收站列出 2 条", entries.Count == 2, $"实际 {entries.Count}");

        // 还原：内容 + 修改时间一致
        var restore = store.Restore(m1.Entry!, null);
        H.Check("还原成功", restore.Success, restore.Error);
        H.Check("还原后内容一致", H.ReadFile(f1) == "111");
        H.Check("还原后修改时间一致", Math.Abs((File.GetLastWriteTimeUtc(PathHelper.ToLong(f1)) - before).TotalSeconds) < 2);

        // 永久删除
        var perm = store.DeleteEntryPermanently(m2.Entry!);
        H.Check("回收站永久删除成功", perm.Success, perm.Error);

        // 长路径 / 保留名 / 尾随点 进回收站
        string deep = H.BuildDeepPath(Path.Combine(root, "deep"), 8, 45);
        string deepFile = Path.Combine(deep, "deep.txt");
        H.WriteFile(deepFile, "D");
        var dm = store.MoveIn(deepFile);
        H.Check("长路径文件移入回收站", dm.Success, dm.Error);
        H.Check("长路径文件源已消失", !H.Exists(deepFile));

        string reservedFile = Path.Combine(root, "con.txt");
        H.WriteFile(reservedFile, "C");
        var rm = store.MoveIn(reservedFile);
        H.Check("保留名文件移入回收站", rm.Success, rm.Error);

        string trailingFile = Path.Combine(root, "trail.");
        H.WriteFile(trailingFile, "T");
        var tm = store.MoveIn(trailingFile);
        H.Check("尾随点文件移入回收站", tm.Success, tm.Error);
        H.Check("尾随点源已消失", !H.Exists(trailingFile));

        // 撤销 / 重做：内存历史栈 + 桥接
        HistoryRoundTrip();

        // 清空
        int emptied = store.Empty(testRoot);
        H.Check("清空回收站删除条目", emptied >= 1, $"清空 {emptied} 项");
    }

    private static void HistoryRoundTrip()
    {
        string root = H.NewDir("history");
        string src = Path.Combine(root, "h-src.txt");
        string dst = Path.Combine(root, "h-dst.txt");
        H.WriteFile(src, "H");
        DateTime t = File.GetLastWriteTimeUtc(PathHelper.ToLong(src));

        FileOperations.Rename(PathHelper.ToLong(src), "h-dst.txt");
        H.Check("历史前置：改名完成", !H.Exists(src) && H.Exists(dst));

        var history = new OperationHistory();
        var bridge = new CoreHistoryBridge();
        history.Push(new RenameHistoryEntry(src, dst, t));
        H.Check("撤销栈有 1 步", history.UndoCount == 1);

        HistoryStepResult undo = history.Undo(bridge);
        H.Check("撤销重命名成功", undo.Success, undo.Message);
        H.Check("撤销后回到原名", H.Exists(src) && !H.Exists(dst));
        H.Check("重做栈有 1 步", history.RedoCount == 1);

        HistoryStepResult redo = history.Redo(bridge);
        H.Check("重做重命名成功", redo.Success, redo.Message);
        H.Check("重做后再次改名", !H.Exists(src) && H.Exists(dst));

        // 外部改动 → 跳过
        H.WriteFile(dst, "changed");
        File.SetLastWriteTimeUtc(PathHelper.ToLong(dst), DateTime.UtcNow.AddMinutes(10));
        HistoryStepResult skipped = history.Undo(bridge);
        H.Check("外部改动后撤销被跳过", skipped.Skipped, skipped.Message);
    }


    private static void ConflictAndCrossVolume()
    {
        string root = H.NewDir("conflict");
        string src = Path.Combine(root, "src");
        string dst = Path.Combine(root, "dst");
        H.MakeDir(src);
        H.MakeDir(dst);

        string s = Path.Combine(src, "dup.txt");
        string d = Path.Combine(dst, "dup.txt");
        H.WriteFile(s, "SOURCE");
        H.WriteFile(d, "TARGET");

        // 冲突：跳过
        var jobSkip = new OperationJob(OperationKind.Copy).AddSource(s, dst);
        jobSkip.ConflictResolver = _ => Task.FromResult(ConflictDecision.Skip());
        OperationResult skipResult = OperationEngine.Execute(jobSkip);
        H.Check("冲突跳过：目标不变", H.ReadFile(d) == "TARGET", skipResult.Message);
        H.Check("冲突跳过：源保留", H.ReadFile(s) == "SOURCE");

        // 冲突：替换
        var jobReplace = new OperationJob(OperationKind.Copy).AddSource(s, dst);
        jobReplace.ConflictResolver = _ => Task.FromResult(ConflictDecision.Replace());
        OperationEngine.Execute(jobReplace);
        H.Check("冲突替换：目标更新", H.ReadFile(d) == "SOURCE");

        // 冲突：保留两者
        H.WriteFile(d, "TARGET2");
        var jobKeep = new OperationJob(OperationKind.Copy).AddSource(s, dst);
        jobKeep.ConflictResolver = _ => Task.FromResult(ConflictDecision.KeepBoth());
        OperationEngine.Execute(jobKeep);
        H.Check("冲突保留两者：原名保留", H.ReadFile(d) == "TARGET2");
        bool keepBoth = Directory.EnumerateFiles(PathHelper.ToLong(dst)).Any(p => Path.GetFileName(p).StartsWith("dup ("));
        H.Check("冲突保留两者：生成副本名", keepBoth);

        // 跨卷移动 ForceCrossVolume：复制成功后删源
        string crossSrc = Path.Combine(root, "cross-src");
        string crossDst = Path.Combine(root, "cross-dst");
        H.MakeDir(crossSrc);
        H.MakeDir(crossDst);
        string cf = Path.Combine(crossSrc, "cross.txt");
        H.WriteFile(cf, "CROSS");
        var jobCross = new OperationJob(OperationKind.Move).AddSource(cf, crossDst);
        jobCross.Options.ForceCrossVolume = true;
        OperationResult crossResult = OperationEngine.Execute(jobCross);
        H.Check("ForceCrossVolume 移动成功", crossResult.Success, crossResult.Message);
        H.Check("跨卷移动：目标存在", H.Exists(Path.Combine(crossDst, "cross.txt")));
        H.Check("跨卷移动：源删除", !H.Exists(cf));

        // 跨卷移动目录：自底向上删源
        string dirSrc = Path.Combine(root, "cdir-src");
        H.MakeDir(Path.Combine(dirSrc, "inner"));
        H.WriteFile(Path.Combine(dirSrc, "inner", "x.txt"), "X");
        string dirDst = Path.Combine(root, "cdir-dst");
        H.MakeDir(dirDst);
        var jobDir = new OperationJob(OperationKind.Move).AddSource(dirSrc, dirDst);
        jobDir.Options.ForceCrossVolume = true;
        OperationResult dirResult = OperationEngine.Execute(jobDir);
        H.Check("跨卷移动目录成功", dirResult.Success, dirResult.Message);
        H.Check("跨卷移动目录：内容落地", H.Exists(Path.Combine(dirDst, "cdir-src", "inner", "x.txt")));
        H.Check("跨卷移动目录：源删除", !H.Exists(dirSrc));

        // 中途取消：目标无残留
        string cancelSrc = Path.Combine(root, "cancel-src");
        H.MakeDir(cancelSrc);
        string big = Path.Combine(cancelSrc, "big.bin");
        H.WriteBytes(big, new byte[8 * 1024 * 1024]);
        string cancelDst = Path.Combine(root, "cancel-dst");
        H.MakeDir(cancelDst);
        var jobCancel = new OperationJob(OperationKind.Copy).AddSource(big, cancelDst);
        jobCancel.ProgressReporter = new Progress<OperationProgress>(_ => jobCancel.Cancel());
        OperationResult cancelResult = OperationEngine.Execute(jobCancel);
        H.Check("取消后任务状态为已取消或完成", cancelResult.Canceled || cancelResult.State == OperationState.Completed);
        bool noResidue = !Directory.EnumerateFiles(PathHelper.ToLong(cancelDst)).Any(p => p.EndsWith(".cjtmp", StringComparison.OrdinalIgnoreCase));
        H.Check("取消后目标无 .cjtmp 残留", noResidue);
    }


    private static void DropAndHitRules()
    {
        // 同盘 = 移动、跨盘 = 复制
        H.Eq("同盘默认移动", DropAction.Move, DropRules.ResolveDefaultEffect(sameVolume: true, ctrl: false, shift: false, alt: false));
        H.Eq("跨盘默认复制", DropAction.Copy, DropRules.ResolveDefaultEffect(sameVolume: false, ctrl: false, shift: false, alt: false));
        H.Eq("Ctrl = 复制", DropAction.Copy, DropRules.ResolveDefaultEffect(sameVolume: true, ctrl: true, shift: false, alt: false));
        H.Eq("Shift = 移动", DropAction.Move, DropRules.ResolveDefaultEffect(sameVolume: false, ctrl: false, shift: true, alt: false));
        H.Eq("Alt = 快捷方式", DropAction.Shortcut, DropRules.ResolveDefaultEffect(sameVolume: true, ctrl: false, shift: false, alt: true));
        H.Eq("Ctrl 优先于 Alt", DropAction.Copy, DropRules.ResolveDefaultEffect(sameVolume: true, ctrl: true, shift: false, alt: true));

        // 拖到自身 / 子文件夹禁止
        H.Check("拖到自身禁止", DropRules.IsForbiddenDrop(@"C:\a", @"C:\a"));
        H.Check("拖到子文件夹禁止", DropRules.IsForbiddenDrop(@"C:\a", @"C:\a\sub"));
        H.Check("兄弟目录允许", !DropRules.IsForbiddenDrop(@"C:\a", @"C:\b"));
        H.Check("拖到自身子串不误判", !DropRules.IsForbiddenDrop(@"C:\ab", @"C:\abc"));

        DropDecision d = DropRules.Decide(new[] { @"C:\a\x.txt" }, @"C:\b", "b", ctrl: true, shift: false, alt: false, sameVolume: true);
        H.Check("Decide 有效且为复制", d.IsValid && d.Action == DropAction.Copy, d.Tooltip);

        // 背景菜单命中规则：ListHitTest 附加属性
        H.Check("ListHitTest 名称内容默认为假", !ListHitTest.IsNameContentHit(null));

        // ResolveDrop 对非法路径不抛，TryToLong 保护
        DropDecision bad = DropRules.Decide(new[] { "C:relative" }, @"C:\b", "b", ctrl: false, shift: false, alt: false);
        H.Check("非法源路径不抛异常", !bad.Forbidden || bad.Action == DropAction.None);
    }


    private static void GroupingAndSearch()
    {
        // 分组规则：名称
        FileGroupKey keyA = FileGroupRules.GetKey(FileGroupBy.Name, "Apple.txt", false, DateTime.Now, 10UL, "文本文档", "", false, DateTime.Now);
        FileGroupKey keyN = FileGroupRules.GetKey(FileGroupBy.Name, "123.txt", false, DateTime.Now, 10UL, "文本文档", "", false, DateTime.Now);
        H.Eq("名称分组 A", "A-H", keyA.Name);
        H.Eq("名称分组 数字", "0-9", keyN.Name);

        // 分组规则：大小，文件夹单独组
        FileGroupKey folderKey = FileGroupRules.GetKey(FileGroupBy.Size, "f", true, DateTime.Now, 0UL, "文件夹", "", false, DateTime.Now);
        H.Eq("大小分组：文件夹单独", FileGroupRules.FolderGroupName, folderKey.Name);

        // 分组规则：修改日期，可注入 Now
        DateTime now = new(2026, 10, 21, 12, 0, 0);
        FileGroupKey today = FileGroupRules.GetKey(FileGroupBy.DateModified, "x", false, now.AddHours(-1), 0UL, "", "", false, now);
        H.Eq("日期分组：今天", "今天", today.Name);

        // 搜索：通配符 + 字面匹配
        H.Check("通配符识别", RecursiveSearchService.IsWildcardPattern("*.txt"));
        H.Check("通配符匹配", RecursiveSearchService.NameMatches("a.txt", "*.txt"));
        H.Check("字面匹配忽略大小写", RecursiveSearchService.NameMatches("Hello.txt", "hello"));

        // 递归搜索真实目录，含子目录
        string root = H.NewDir("search");
        H.WriteFile(Path.Combine(root, "a-match.txt"), "1");
        H.WriteFile(Path.Combine(root, "b.txt"), "2");
        string sub = Path.Combine(root, "sub");
        H.MakeDir(sub);
        H.WriteFile(Path.Combine(sub, "c-match.txt"), "3");

        var service = new RecursiveSearchService();
        var hits = new List<SearchHit>();
        SearchSummary summary = service.Search(root, "*match*", batch => hits.AddRange(batch), null, CancellationToken.None);
        H.Check("递归搜索找到 2 项", hits.Count == 2, $"实际 {hits.Count}");
        H.Check("搜索未取消", !summary.Canceled);

        // 暂停 / 继续：暂停时不丢结果
        using var pauseToken = new SearchPauseToken();
        var hits2 = new List<SearchHit>();
        Task<SearchSummary> task = Task.Run(() => service.Search(
            root, "*.txt", batch => { lock (hits2) { hits2.AddRange(batch); } }, null, CancellationToken.None, pauseToken));
        pauseToken.Pause();
        Thread.Sleep(120);
        pauseToken.Resume();
        H.Check("继续后搜索完成", task.Wait(TimeSpan.FromSeconds(10)));
        lock (hits2)
        {
            H.Check("暂停 / 继续后结果完整", hits2.Count == 3, $"实际 {hits2.Count}");
        }
    }


    private static void PathRepairAndPreview()
    {
        // 修复路径规则
        H.Check("检测保留名", PathRepairRules.DetectIssues("con.txt", @"C:\con.txt") != PathRepairIssueKind.None);
        H.Check("检测尾随点", (PathRepairRules.DetectIssues("a.", @"C:\a.") & PathRepairIssueKind.TrailingDot) != 0);
        string longName = new string('x', 300) + ".txt";
        H.Check("检测超长", (PathRepairRules.DetectIssues(longName, @"C:\" + longName) & PathRepairIssueKind.TooLong) != 0);

        PathRepairSuggestion sug = PathRepairRules.Suggest("con.txt", @"C:\", false);
        H.Check("保留名建议加下划线", sug.Name.StartsWith("con_", StringComparison.OrdinalIgnoreCase), sug.Name);

        PathRepairValidation invalid = PathRepairRules.Validate("a<b", @"C:\", null);
        H.Check("修复名非法字符被拒", !invalid.IsValid);

        // 预览三类分类
        H.Eq("图片分类", PreviewKind.Image, PreviewTypeRules.Classify("a.png", false));
        H.Eq("文本分类", PreviewKind.Text, PreviewTypeRules.Classify("a.txt", false));
        H.Eq("PDF 分类", PreviewKind.Pdf, PreviewTypeRules.Classify("a.pdf", false));
        H.Eq("其他分类", PreviewKind.Other, PreviewTypeRules.Classify("a.xyz", false));
        H.Eq("文件夹分类", PreviewKind.Other, PreviewTypeRules.Classify("folder", true));

        // 系统处理器回落：不抛异常即可，无处理器时返回 null → 信息页
        bool noThrow = true;
        try
        {
            _ = ChangJing.App.Core.Shell.PreviewHandlerRegistry.Find(".xyz");
            _ = ChangJing.App.Core.Shell.PreviewHandlerRegistry.Find(".txt");
        }
        catch
        {
            noThrow = false;
        }

        H.Check("预览处理器查询不抛异常", noThrow);

        // 关键词高亮
        IReadOnlyList<HighlightSegment> segments = SearchHighlight.Compute("hello world", "world");
        H.Check("高亮命中 1 段", segments.Count(s => s.IsMatch) == 1);
        H.Check("高亮片段偏移正确", segments.First(s => s.IsMatch).Start == 6);

        // 自然排序
        H.Check("自然排序 2 < 10", NaturalComparer.Compare("file2.txt", "file10.txt") < 0);

        // 设置坏值清理：通过 Load 走 Sanitize
        string badPath = Path.Combine(H.Root, "settings-bad.json");
        File.WriteAllText(badPath, "{\"IconSize\": -5, \"ViewMode\": \"Bogus\", \"ThemeMode\": \"??\"}");
        var badSvc = new SettingsService(badPath);
        AppSettings badSettings = badSvc.Load();
        H.Check("坏图标尺寸被夹取", !double.IsNaN(badSettings.IconSize) && badSettings.IconSize >= AppSettings.MinIconSize,
            $"实际 {badSettings.IconSize}");
        H.Check("坏视图名回退", badSettings.ViewMode == AppSettings.DefaultViewMode, badSettings.ViewMode);
        H.Check("坏主题名回退", badSettings.ThemeMode == AppSettings.ThemeModeSystem, badSettings.ThemeMode);
    }

    // 辅助

    /// <summary>轮询等待后台复制 / 移动落地，FileTransferService 是非阻塞入队</summary>
    private static bool PumpOpsUntil(Func<bool> condition, int timeoutMs = 15000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return condition();
    }

    /// <summary>历史栈的验证桥：直接用核心层原语实现 <see cref="IHistoryOperations"/></summary>
    private sealed class CoreHistoryBridge : IHistoryOperations
    {
        private readonly FileTransferService _transfer = new();

        public bool TryGetPathInfo(string displayPath, out DateTime lastWriteUtc, out bool isDirectory)
        {
            string longPath = PathHelper.ToLong(displayPath);
            isDirectory = Directory.Exists(longPath);
            if (FileOperations.TryGetEntryInfo(longPath, out _, out _, out lastWriteUtc))
            {
                return true;
            }

            lastWriteUtc = DateTime.MinValue;
            return false;
        }

        public FileOperationResult Rename(string sourceDisplayPath, string newName)
            => FileOperations.Rename(PathHelper.ToLong(sourceDisplayPath), newName);

        public FileTransferResult Move(IReadOnlyList<string> sourceDisplayPaths, string targetFolderDisplayPath)
            => _transfer.Move(sourceDisplayPaths, targetFolderDisplayPath);

        public FileTransferResult Copy(IReadOnlyList<string> sourceDisplayPaths, string targetFolderDisplayPath)
            => _transfer.Copy(sourceDisplayPaths, targetFolderDisplayPath);

        public HistoryDeleteResult DeleteToRecycleBin(IReadOnlyList<string> displayPaths)
            => HistoryDeleteResult.Ok("验证桥：未实现删除。");

        public HistoryRestoreResult RestoreFromRecycleBin(IReadOnlyList<RecycleBinEntry> entries)
            => HistoryRestoreResult.Ok("验证桥：未实现还原。");

        public bool TryWriteFile(string displayPath, byte[] content, DateTime lastWriteUtc)
        {
            try
            {
                H.WriteBytes(displayPath, content);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public FileOperationResult CreateDirectory(string parentDisplayPath, string name)
            => FileOperations.CreateDirectory(parentDisplayPath, name);
    }
}
