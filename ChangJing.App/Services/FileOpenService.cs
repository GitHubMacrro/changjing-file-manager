using ChangJing.App.Core.LongPath;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Services;

public enum FileOpenMode
{
    /// <summary>短路径 + 正常名称，直接用系统默认程序打开</summary>
    Direct,

    /// <summary>路径过长：在程序自有短根目录下建目录联接后打开</summary>
    ShortRoot,

    /// <summary>名称特殊或无法建联接：复制到临时目录用合法名打开</summary>
    TempCopy,
}

public readonly struct FileOpenResult
{
    private FileOpenResult(bool success, string error, FileOpenMode mode, TempOpenCopy.CopyRecord? tempCopy)
    {
        Success = success;
        Error = error;
        Mode = mode;
        TempCopy = tempCopy;
    }

    /// <summary>是否成功发起打开</summary>
    public bool Success { get; }

    /// <summary>失败原因，中文；成功时为空串</summary>
    public string Error { get; }

    public FileOpenMode Mode { get; }

    /// <summary>临时副本方式的副本记录；其它方式为 null</summary>
    public TempOpenCopy.CopyRecord? TempCopy { get; }

    internal static FileOpenResult Ok(FileOpenMode mode, TempOpenCopy.CopyRecord? tempCopy = null)
        => new(true, string.Empty, mode, tempCopy);

    internal static FileOpenResult Failed(string error) => new(false, error, FileOpenMode.Direct, null);
}

/// <summary>短路径直接打开，长路径走短根联接，否则临时副本</summary>
// 外部打开不再用 DefineDosDevice 映射盘符，盘符会出现在「此电脑」里很怪
public static class FileOpenService
{
    /// <summary>打开一个文件的普通路径</summary>
    public static FileOpenResult Open(string displayPath)
    {
        if (string.IsNullOrWhiteSpace(displayPath))
        {
            return FileOpenResult.Failed("路径为空。");
        }

        string display = PathHelper.ToDisplay(displayPath);
        bool tooLong = LongPathRules.IsTooLong(display);
        string name = PathHelper.GetName(display);
        bool specialName = LongPathRules.HasTrailingDotOrSpace(name) || LongPathRules.IsReservedName(name);

        if (!tooLong && !specialName)
        {
            return ShellOpener.TryOpen(display, out ShellOpenResult direct)
                ? FileOpenResult.Ok(FileOpenMode.Direct)
                : FileOpenResult.Failed(direct.Error);
        }

        // 名称特殊：Win32 无法打开该名字，必须用临时副本
        if (specialName)
        {
            return OpenViaTempCopy(display);
        }

        if (OpenShortRoot.TryPrepareFile(display, out string? shortPath)
            && !string.IsNullOrEmpty(shortPath))
        {
            if (ShellOpener.TryOpen(shortPath!, out ShellOpenResult shortResult) && shortResult.Success)
            {
                return FileOpenResult.Ok(FileOpenMode.ShortRoot);
            }

            LogManager.Info(
                typeof(FileOpenService),
                $"短根联接打开失败，回退临时副本：{display} — {shortResult.Error}");
        }

        return OpenViaTempCopy(display);
    }

    private static FileOpenResult OpenViaTempCopy(string displayPath)
    {
        if (!TempOpenCopy.TryCopy(displayPath, out string? copied, out string error, out TempOpenCopy.CopyRecord? record)
            || string.IsNullOrEmpty(copied))
        {
            return FileOpenResult.Failed(error);
        }

        if (ShellOpener.TryOpen(copied!, out ShellOpenResult result) && result.Success)
        {
            return FileOpenResult.Ok(FileOpenMode.TempCopy, record);
        }

        return FileOpenResult.Failed(result.Error);
    }
}
