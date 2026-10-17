using ChangJing.App.Core.FileSystem;
using ChangJing.App.Core.LongPath;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Core.Operations;

/// <summary>一次新建目录 / 重命名的结果</summary>
public readonly struct FileOperationResult
{
    private FileOperationResult(bool success, int errorCode, string error, string displayPath, string longPath)
    {
        Success = success;
        ErrorCode = errorCode;
        Error = error;
        DisplayPath = displayPath;
        LongPath = longPath;
    }

    public bool Success { get; }

    /// <summary>Win32 错误码，成功时为 0</summary>
    public int ErrorCode { get; }

    /// <summary>失败原因，成功时为空串</summary>
    public string Error { get; }

    /// <summary>结果路径的普通形态，失败时为空串</summary>
    public string DisplayPath { get; }

    /// <summary>结果路径的扩展形态，失败时为空串</summary>
    public string LongPath { get; }

    public bool IsFailed => !Success;

    internal static FileOperationResult Ok(string displayPath, string longPath)
        => new(true, 0, string.Empty, displayPath, longPath);

    internal static FileOperationResult Failed(int errorCode, string error)
        => new(false, errorCode, error, string.Empty, string.Empty);
}

/// <summary>基础文件系统写操作：新建目录、重命名</summary>
// 写操作走扩展路径，不用会裁剪结尾点的 .NET API
public static class FileOperations
{
    public const string DefaultFolderName = "新建文件夹";

    private const int MaxNameAttempts = 9999;

    /// <summary>在父目录下创建不重名的文件夹</summary>
    public static FileOperationResult CreateDirectory(string parentDisplayPath, string? baseName = null)
    {
        if (string.IsNullOrWhiteSpace(parentDisplayPath))
        {
            return FileOperationResult.Failed(Win32ErrorMessages.ErrorPathNotFound, "父文件夹路径为空。");
        }

        string baseFolderName = string.IsNullOrWhiteSpace(baseName) ? DefaultFolderName : baseName!;

        string parentLong;
        try
        {
            parentLong = PathHelper.ToLong(parentDisplayPath);
        }
        catch (ArgumentException ex)
        {
            LogManager.Warning(typeof(FileOperations), $"新建文件夹失败（父目录路径不可用）：{ex.Message}");
            return FileOperationResult.Failed(Win32ErrorMessages.ErrorPathNotFound, ex.Message);
        }

        for (int index = 0; index < MaxNameAttempts; index++)
        {
            string candidateName = index == 0 ? baseFolderName : $"{baseFolderName} ({index + 1})";
            string candidateLong = PathHelper.Combine(parentLong, candidateName);

            if (NativeMethods.CreateDirectoryW(candidateLong, IntPtr.Zero))
            {
                string display = PathHelper.ToDisplay(candidateLong);
                LogManager.Info(typeof(FileOperations), $"已新建文件夹：{display}");
                return FileOperationResult.Ok(display, candidateLong);
            }

            int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();

            // 已存在同名目录，换下一个序号继续尝试
            if (error == Win32ErrorMessages.ErrorAlreadyExists)
            {
                continue;
            }

            string message = Win32ErrorMessages.Describe(error);
            LogManager.Warning(
                typeof(FileOperations),
                $"新建文件夹失败：{PathHelper.ToDisplay(candidateLong)} — {message}（错误码 {error}）");
            return FileOperationResult.Failed(error, message);
        }

        string tooMany = $"无法生成不重名的名称（已尝试 {MaxNameAttempts} 次）。";
        LogManager.Warning(typeof(FileOperations), tooMany);
        return FileOperationResult.Failed(Win32ErrorMessages.ErrorAlreadyExists, tooMany);
    }

    public static FileOperationResult Rename(string sourceLongPath, string newName)
    {
        if (string.IsNullOrEmpty(sourceLongPath) || string.IsNullOrEmpty(newName))
        {
            return FileOperationResult.Failed(Win32ErrorMessages.ErrorInvalidName, "重命名参数不完整。");
        }

        string? parent = PathHelper.GetParent(sourceLongPath);
        if (parent is null)
        {
            string rootError = "无法重命名所在盘的根。";
            LogManager.Warning(typeof(FileOperations), rootError);
            return FileOperationResult.Failed(Win32ErrorMessages.ErrorAccessDenied, rootError);
        }

        string targetLong = PathHelper.Combine(parent, newName);
        string sourceName = PathHelper.GetName(sourceLongPath);
        bool caseOnlyChange = string.Equals(sourceName, newName, StringComparison.OrdinalIgnoreCase);

        // 目标已存在直接失败，避免隐式覆盖差异
        if (!caseOnlyChange && Exists(targetLong))
        {
            string existsMessage = Win32ErrorMessages.Describe(Win32ErrorMessages.ErrorAlreadyExists);
            LogManager.Warning(
                typeof(FileOperations),
                $"重命名失败（目标已存在）：{PathHelper.ToDisplay(sourceLongPath)} → {PathHelper.ToDisplay(targetLong)}");
            return FileOperationResult.Failed(Win32ErrorMessages.ErrorAlreadyExists, existsMessage);
        }

        if (NativeMethods.MoveFileExW(sourceLongPath, targetLong, dwFlags: 0))
        {
            string display = PathHelper.ToDisplay(targetLong);
            LogManager.Info(
                typeof(FileOperations),
                $"已重命名：{PathHelper.ToDisplay(sourceLongPath)} → {display}");
            return FileOperationResult.Ok(display, targetLong);
        }

        int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        string errorMessage = Win32ErrorMessages.Describe(error);
        LogManager.Warning(
            typeof(FileOperations),
            $"重命名失败：{PathHelper.ToDisplay(sourceLongPath)} → {PathHelper.ToDisplay(targetLong)} — " +
            $"{errorMessage}（错误码 {error}）");

        return FileOperationResult.Failed(error, errorMessage);
    }

    public static bool TryGetEntryInfo(
        string longPath,
        out uint attributes,
        out ulong size,
        out DateTime lastWriteTimeUtc)
    {
        attributes = NativeMethods.InvalidFileAttributes;
        size = 0;
        lastWriteTimeUtc = DateTime.MinValue;

        try
        {
            if (!NativeMethods.GetFileAttributesExW(
                    longPath,
                    NativeMethods.GetFileExInfoStandard,
                    out NativeMethods.WIN32_FILE_ATTRIBUTE_DATA data))
            {
                return false;
            }

            attributes = data.dwFileAttributes;
            bool isDirectory = (attributes & NativeMethods.FileAttributeDirectory) != 0;
            size = isDirectory ? 0UL : data.Size;
            lastWriteTimeUtc = data.LastWriteTimeUtc;
            return true;
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(FileOperations), $"查询条目属性失败（{PathHelper.ToDisplay(longPath)}）：{ex.Message}");
            return false;
        }
    }

    /// <summary>查询并构造 <see cref="FileEntry"/>，失败返回 null</summary>
    public static FileEntry? TryCreateEntry(string longPath)
    {
        if (string.IsNullOrEmpty(longPath))
        {
            return null;
        }

        if (!TryGetEntryInfo(longPath, out uint attributes, out ulong size, out DateTime lastWriteTimeUtc))
        {
            return null;
        }

        return new FileEntry(PathHelper.GetName(longPath), longPath, attributes, size, lastWriteTimeUtc);
    }

    /// <summary>判断路径是否存在，重名校验兜底</summary>
    public static bool Exists(string longPath)
    {
        if (string.IsNullOrEmpty(longPath))
        {
            return false;
        }

        try
        {
            return NativeMethods.GetFileAttributesExW(
                longPath,
                NativeMethods.GetFileExInfoStandard,
                out NativeMethods.WIN32_FILE_ATTRIBUTE_DATA _);
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(FileOperations), $"查询路径是否存在失败（{PathHelper.ToDisplay(longPath)}）：{ex.Message}");
            return false;
        }
    }
}
