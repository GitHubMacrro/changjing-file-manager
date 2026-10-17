using ChangJing.App.Core.FileSystem;
using ChangJing.App.Core.LongPath;
using ChangJing.App.Core.Operations;

namespace ChangJing.App.Services;

/// <summary>启动命令行位置参数解析出的目标类型</summary>
public enum StartupTargetKind
{
    /// <summary>默认位置（此电脑）</summary>
    Default,

    /// <summary>打开指定文件夹</summary>
    Folder,

    /// <summary>打开文件所在文件夹并选中该文件</summary>
    File,
}

/// <summary>一次启动要打开的目标：文件夹 / 文件 / 默认位置 + 提示</summary>
public sealed class StartupTarget
{
    private StartupTarget(StartupTargetKind kind, string folderPath, string? selectItemPath, string? message)
    {
        Kind = kind;
        FolderPath = folderPath;
        SelectItemPath = selectItemPath;
        Message = message;
    }

    public StartupTargetKind Kind { get; }

    /// <summary>要打开的文件夹普通路径，Default 时为空串</summary>
    public string FolderPath { get; }

    /// <summary>文件类型时要选中的文件普通路径，其余为 null</summary>
    public string? SelectItemPath { get; }

    /// <summary>面向用户的中文提示，无提示时为 null</summary>
    public string? Message { get; }

    /// <summary>默认位置（此电脑）</summary>
    public static StartupTarget Default() => new(StartupTargetKind.Default, string.Empty, null, null);

    /// <summary>打开文件夹</summary>
    public static StartupTarget ForFolder(string folderDisplayPath)
        => new(StartupTargetKind.Folder, folderDisplayPath, null, null);

    /// <summary>打开父文件夹并选中文件</summary>
    public static StartupTarget ForFile(string folderDisplayPath, string selectItemDisplayPath)
        => new(StartupTargetKind.File, folderDisplayPath, selectItemDisplayPath, null);

    /// <summary>路径不存在：默认位置 + 中文提示</summary>
    public static StartupTarget NotFound(string inputDisplayPath)
        => new(StartupTargetKind.Default, string.Empty, null, $"找不到路径“{inputDisplayPath}”，已在默认位置打开。");
}

/// <summary>启动命令行位置参数解析：支持文件夹 / 盘符 / 文件 / UNC / 长路径 / \\?\ 前缀</summary>
// 只解析目标，不做窗口复用；每次启动都开新窗口
public static class CommandLineStartup
{
    /// <summary>解析启动参数；无位置参数返回默认位置</summary>
    public static StartupTarget Resolve(string[]? args)
    {
        string? raw = ExtractPositionalArgument(args);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return StartupTarget.Default();
        }

        if (!PathHelper.TryParseUserInput(raw, out PathParseResult parsed))
        {
            return StartupTarget.NotFound(DisplayFallback(raw!));
        }

        if (!FileOperations.TryGetEntryInfo(parsed.LongPath, out uint attributes, out _, out _))
        {
            return StartupTarget.NotFound(parsed.DisplayPath);
        }

        if ((attributes & NativeMethods.FileAttributeDirectory) != 0)
        {
            return StartupTarget.ForFolder(parsed.DisplayPath);
        }

        string? parentLong = PathHelper.GetParent(parsed.LongPath);
        string parentDisplay = parentLong is null ? parsed.DisplayPath : PathHelper.ToDisplay(parentLong);
        return StartupTarget.ForFile(parentDisplay, parsed.DisplayPath);
    }

    /// <summary>取第一个非选项参数；跳过 --folder 及其值（提权交接用）</summary>
    private static string? ExtractPositionalArgument(string[]? args)
    {
        if (args is null)
        {
            return null;
        }

        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index] ?? string.Empty;

            if (string.Equals(arg, ElevationService.FolderArgumentName, StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            if (arg.StartsWith(ElevationService.FolderArgumentName + "=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (arg.Length > 0)
            {
                return RepairTrailingBackslashEscape(arg);
            }
        }

        return null;
    }

    /// <summary>修复「尾部反斜杠被命令行转义吃掉」的参数，如 <c>C:"</c> / <c>C:\Some Folder"</c></summary>
    // Windows 命令行里 \" 是转义引号：右键盘 / 磁盘根目录空白处右键传入 "C:\"，程序实际收到 C:"（尾 \ 被吃掉）；
    // 这里去掉残留引号并补回尾 \。自身带成对引号的参数（首字符是 "）交给后续解析，勿动
    private static string RepairTrailingBackslashEscape(string arg)
    {
        if (arg.Length >= 1 && arg[^1] == '"' && arg[0] != '"')
        {
            return arg.Substring(0, arg.Length - 1) + "\\";
        }

        return arg;
    }

    /// <summary>解析失败时尽量给出干净回显：去引号 + 去 \\?\</summary>
    private static string DisplayFallback(string raw)
    {
        string text = raw.Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            text = text.Substring(1, text.Length - 2).Trim();
        }

        return PathHelper.ToDisplay(text);
    }
}
