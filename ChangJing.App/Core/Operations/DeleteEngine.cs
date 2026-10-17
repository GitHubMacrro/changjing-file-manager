using System.Runtime.InteropServices;
using ChangJing.App.Core.FileSystem;
using ChangJing.App.Core.LongPath;
using ChangJing.App.Services;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Core.Operations;

/// <summary>永久删除一个文件或目录树的结果</summary>
public sealed class DeleteTreeResult
{
    private readonly List<OperationError> _errors = new();

    public bool Canceled { get; internal set; }

    public int DeletedFiles { get; internal set; }

    public long DeletedBytes { get; internal set; }

    /// <summary>失败项，单项失败后继续收集</summary>
    public IReadOnlyList<OperationError> Errors => _errors;

    public bool Success => !Canceled && _errors.Count == 0;

    internal void AddError(OperationError error) => _errors.Add(error);
}

/// <summary>永久删除引擎，不进回收站</summary>
// 显式栈迭代防栈溢出，重解析点只删链接本身
public static class DeleteEngine
{
    public static DeleteTreeResult DeleteTree(
        string longPath,
        bool isDirectory,
        string displayName,
        CancellationToken cancellationToken,
        Action<string, long>? onItemDeleted = null)
    {
        var result = new DeleteTreeResult();

        try
        {
            if (!isDirectory)
            {
                DeleteFile(longPath, displayName, cancellationToken, result, onItemDeleted);
                return result;
            }

            DeleteDirectoryTree(longPath, displayName, cancellationToken, result, onItemDeleted);
            return result;
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(DeleteEngine), ex);
            result.AddError(new OperationError(
                OperationKind.Delete,
                PathHelper.GetName(longPath),
                PathHelper.ToDisplay(longPath),
                "删除时发生内部错误，详细信息已写入日志。",
                -1,
                PathHelper.ToDisplay(longPath),
                string.Empty));
            return result;
        }
    }

    /// <summary>供回收站复用，返回第一个错误</summary>
    internal static OperationError? DeleteTreeStatic(
        string longPath,
        bool isDirectory,
        string displayName,
        CancellationToken cancellationToken)
    {
        DeleteTreeResult result = DeleteTree(longPath, isDirectory, displayName, cancellationToken);
        return result.Errors.Count > 0 ? result.Errors[0] : null;
    }

    /// <summary>是否符号链接或目录联接</summary>
    // 重解析点不统计不下钻，防无限递归
    internal static bool IsReparsePoint(string longPath)
        => FileOperations.TryGetEntryInfo(longPath, out uint attributes, out _, out _)
           && (attributes & NativeMethods.FileAttributeReparsePoint) != 0;

    private static void DeleteDirectoryTree(
        string rootLong,
        string displayName,
        CancellationToken cancellationToken,
        DeleteTreeResult result,
        Action<string, long>? onItemDeleted)
    {
        // 根是链接时只删链接本身，不进目标
        uint rootAttributes = NativeMethods.GetFileAttributesW(rootLong);
        if (rootAttributes != NativeMethods.InvalidFileAttributes
            && (rootAttributes & 0x400u /* FILE_ATTRIBUTE_REPARSE_POINT */) != 0)
        {
            if (!RemoveDirectory(rootLong, out int linkError, out string linkReason)
                && linkError != Win32ErrorMessages.ErrorFileNotFound
                && linkError != Win32ErrorMessages.ErrorPathNotFound)
            {
                result.AddError(CreateError(rootLong, linkReason, linkError));
            }

            return;
        }

        var enumerator = new DirectoryEnumerator(200);
        var pending = new Stack<string>();
        var directoriesBottomUp = new List<string>();

        pending.Push(rootLong);

        while (pending.Count > 0)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                result.Canceled = true;
                return;
            }

            string dir = pending.Pop();
            directoriesBottomUp.Add(dir);

            var (items, enumResult) = enumerator.ToList(dir, cancellationToken);

            if (enumResult.Canceled)
            {
                result.Canceled = true;
                return;
            }

            if (!enumResult.Success)
            {
                // 枚举失败记录后继续处理其它目录
                result.AddError(CreateError(dir, enumResult.Error, enumResult.ErrorCode));
                continue;
            }

            foreach (FileEntry item in items)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    result.Canceled = true;
                    return;
                }

                if (item.IsDirectory)
                {
                    // 重解析点不下钻，只删链接本身
                    if (!item.IsReparsePoint)
                    {
                        pending.Push(item.FullPath);
                    }
                    else
                    {
                        directoriesBottomUp.Add(item.FullPath);
                    }

                    continue;
                }

                DeleteFile(item.FullPath, item.Name, cancellationToken, result, onItemDeleted);
            }
        }

        // 自底向上删目录，倒序保证子先于父
        for (int index = directoriesBottomUp.Count - 1; index >= 0; index--)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                result.Canceled = true;
                return;
            }

            string dir = directoriesBottomUp[index];
            if (!RemoveDirectory(dir, out int error, out string reason))
            {
                // 目录已被其它程序删掉不算失败
                if (error == Win32ErrorMessages.ErrorFileNotFound || error == Win32ErrorMessages.ErrorPathNotFound)
                {
                    continue;
                }

                result.AddError(CreateError(dir, reason, error));
            }
        }

        _ = displayName;
    }

    private static void DeleteFile(
        string longPath,
        string displayName,
        CancellationToken cancellationToken,
        DeleteTreeResult result,
        Action<string, long>? onItemDeleted)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            result.Canceled = true;
            return;
        }

        if (!FileOperations.TryGetEntryInfo(longPath, out uint attributes, out ulong size, out _))
        {
            // 已不存在视为成功
            return;
        }

        // 只读文件删不掉，先清只读位
        if ((attributes & NativeMethods.FileAttributeReadOnly) != 0
            || (attributes & NativeMethods.FileAttributeHidden) != 0
            || (attributes & NativeMethods.FileAttributeSystem) != 0)
        {
            NativeMethods.SetFileAttributesW(longPath, NativeMethods.FileAttributeNormal);
        }

        if (NativeMethods.DeleteFileW(longPath))
        {
            result.DeletedFiles++;
            result.DeletedBytes += (long)size;
            onItemDeleted?.Invoke(displayName, (long)size);
            return;
        }

        int error = Marshal.GetLastWin32Error();

        if (error == Win32ErrorMessages.ErrorFileNotFound || error == Win32ErrorMessages.ErrorPathNotFound)
        {
            return;
        }

        // 只读重试一次，部分文件系统上属性设置会滞后
        if (NativeMethods.SetFileAttributesW(longPath, NativeMethods.FileAttributeNormal)
            && NativeMethods.DeleteFileW(longPath))
        {
            result.DeletedFiles++;
            result.DeletedBytes += (long)size;
            onItemDeleted?.Invoke(displayName, (long)size);
            return;
        }

        error = Marshal.GetLastWin32Error();
        result.AddError(CreateError(longPath, Describe(error, PathHelper.ToDisplay(longPath)), error));
    }

    private static bool RemoveDirectory(string longPath, out int error, out string reason)
    {
        error = 0;
        reason = string.Empty;

        uint attributes = NativeMethods.GetFileAttributesW(longPath);
        if (attributes != NativeMethods.InvalidFileAttributes
            && (attributes & NativeMethods.FileAttributeReadOnly) != 0)
        {
            NativeMethods.SetFileAttributesW(longPath, NativeMethods.FileAttributeNormal);
        }

        if (NativeMethods.RemoveDirectoryW(longPath))
        {
            return true;
        }

        error = Marshal.GetLastWin32Error();
        reason = Describe(error, PathHelper.ToDisplay(longPath));
        return false;
    }

    private static OperationError CreateError(string longPath, string reason, int errorCode)
        => new(
            OperationKind.Delete,
            PathHelper.GetName(longPath),
            PathHelper.ToDisplay(longPath),
            reason,
            errorCode,
            PathHelper.ToDisplay(longPath),
            string.Empty);

    private static string Describe(int errorCode, string displayPath)
    {
        switch (errorCode)
        {
            case Win32ErrorMessages.ErrorFileNotFound:
            case Win32ErrorMessages.ErrorPathNotFound:
                return "源文件已不存在。";

            case Win32ErrorMessages.ErrorInvalidDrive:
            case Win32ErrorMessages.ErrorNotReady:
                return "位置不可用（驱动器不存在或设备未就绪）。";

            case Win32ErrorMessages.ErrorAccessDenied:
                return "没有权限删除此文件或文件夹。";

            case Win32ErrorMessages.ErrorSharingViolation:
            case Win32ErrorMessages.ErrorLockViolation:
            {
                string? locking = null;
                try
                {
                    locking = RestartManagerService.DescribeLockingProcesses(displayPath);
                }
                catch
                {
                    // 占用查询失败不影响主流程
                }

                return locking ?? "文件正被其他程序使用。";
            }

            case Win32ErrorMessages.ErrorDirectoryNotEmpty:
                return "文件夹不为空（内部有未能删除的项目）。";

            default:
                return Win32ErrorMessages.Describe(errorCode);
        }
    }
}
