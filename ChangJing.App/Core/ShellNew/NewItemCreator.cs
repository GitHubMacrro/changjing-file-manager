using System;
using System.ComponentModel;
using System.IO;
using ChangJing.App.Core.FileSystem;
using ChangJing.App.Core.LongPath;
using ChangJing.App.Core.Operations;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Core.ShellNew;

/// <summary>按 ShellNewEntry 在指定目录下创建新文件，用于右键新建子菜单</summary>
// 走扩展路径 \\?\；重名依次尝试 2、3…，非重名失败返回错误码
public static class NewItemCreator
{
    private const int MaxNameAttempts = 9999;

    private const int ErrorAccessDenied = Win32ErrorMessages.ErrorAccessDenied;

    private const int ErrorAlreadyExists = Win32ErrorMessages.ErrorAlreadyExists;

    private const int ErrorFileExists = Win32ErrorMessages.ErrorFileExists;

    /// <summary>按 entry 在 parentDisplayPath 下创建文件</summary>
    public static FileOperationResult CreateFromEntry(
        ShellNewEntry entry,
        string parentDisplayPath,
        string? baseName = null)
    {
        if (entry is null)
        {
            return FileOperationResult.Failed(Win32ErrorMessages.ErrorInvalidParameter, "新建类型为空。");
        }

        if (string.IsNullOrWhiteSpace(parentDisplayPath))
        {
            return FileOperationResult.Failed(Win32ErrorMessages.ErrorPathNotFound, "父文件夹路径为空。");
        }

        string baseFileName = string.IsNullOrWhiteSpace(baseName) ? entry.DefaultFileName : baseName!;
        if (string.IsNullOrWhiteSpace(baseFileName))
        {
            return FileOperationResult.Failed(Win32ErrorMessages.ErrorInvalidName, "文件名称为空。");
        }

        string parentLong;
        try
        {
            parentLong = PathHelper.ToLong(parentDisplayPath);
        }
        catch (ArgumentException ex)
        {
            LogManager.Warning(typeof(NewItemCreator), $"新建文件失败（父目录路径不可用）：{ex.Message}");
            return FileOperationResult.Failed(Win32ErrorMessages.ErrorPathNotFound, ex.Message);
        }

        for (int index = 0; index < MaxNameAttempts; index++)
        {
            string candidateName = index == 0 ? baseFileName : InsertOrdinal(baseFileName, index + 1);
            string candidateLong = PathHelper.Combine(parentLong, candidateName);

            try
            {
                if (TryCreate(entry, candidateLong, out bool alreadyExists, out int errorCode))
                {
                    string display = PathHelper.ToDisplay(candidateLong);
                    LogManager.Info(typeof(NewItemCreator), $"已新建文件：{display}");
                    return FileOperationResult.Ok(display, candidateLong);
                }

                if (alreadyExists)
                {
                    continue;
                }

                string message = DescribeError(errorCode);
                LogManager.Warning(
                    typeof(NewItemCreator),
                    $"新建文件失败：{PathHelper.ToDisplay(candidateLong)} — {message}（错误码 {errorCode}）");
                return FileOperationResult.Failed(errorCode, message);
            }
            catch (Exception ex)
            {
                int code = ExtractErrorCode(ex);
                if (code == ErrorAlreadyExists || code == ErrorFileExists)
                {
                    continue;
                }

                string message = DescribeError(code, ex);
                LogManager.Warning(
                    typeof(NewItemCreator),
                    $"新建文件失败：{PathHelper.ToDisplay(candidateLong)} — {message}（错误码 {code}）");
                return FileOperationResult.Failed(code, message);
            }
        }

        string tooMany = $"无法生成不重名的名称（已尝试 {MaxNameAttempts} 次）。";
        LogManager.Warning(typeof(NewItemCreator), tooMany);
        return FileOperationResult.Failed(ErrorAlreadyExists, tooMany);
    }

    /// <summary>在 parentDisplayPath 下新建文件夹，委托 FileOperations.CreateDirectory</summary>
    public static FileOperationResult CreateDirectory(string parentDisplayPath, string? baseName = null)
        => FileOperations.CreateDirectory(parentDisplayPath, baseName);

    private static bool TryCreate(ShellNewEntry entry, string candidateLong, out bool alreadyExists, out int errorCode)
    {
        alreadyExists = false;
        errorCode = 0;

        switch (entry.Kind)
        {
            case ShellNewKind.FileName:
                return TryCreateFromTemplate(entry, candidateLong, out alreadyExists, out errorCode);

            case ShellNewKind.Data:
                return TryCreateFromData(entry, candidateLong, out alreadyExists, out errorCode);

            case ShellNewKind.NullFile:
            default:
                return TryCreateEmpty(candidateLong, out alreadyExists, out errorCode);
        }
    }

    private static bool TryCreateEmpty(string candidateLong, out bool alreadyExists, out int errorCode)
    {
        alreadyExists = false;
        errorCode = 0;

        try
        {
            using var stream = new FileStream(
                candidateLong,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            return true;
        }
        catch (IOException ex) when (IsAlreadyExists(ex))
        {
            alreadyExists = true;
            return false;
        }
        catch (IOException ex)
        {
            errorCode = ExtractErrorCode(ex);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            errorCode = ErrorAccessDenied;
            return false;
        }
    }

    private static bool TryCreateFromTemplate(
        ShellNewEntry entry,
        string candidateLong,
        out bool alreadyExists,
        out int errorCode)
    {
        alreadyExists = false;
        errorCode = 0;

        // 模板缺失：降级为空文件
        if (string.IsNullOrEmpty(entry.TemplatePath) || !File.Exists(entry.TemplatePath))
        {
            return TryCreateEmpty(candidateLong, out alreadyExists, out errorCode);
        }

        string templateLong;
        try
        {
            templateLong = PathHelper.ToLong(entry.TemplatePath!);
        }
        catch (ArgumentException)
        {
            return TryCreateEmpty(candidateLong, out alreadyExists, out errorCode);
        }

        try
        {
            File.Copy(templateLong, candidateLong, overwrite: false);
            return true;
        }
        catch (IOException ex) when (IsAlreadyExists(ex))
        {
            alreadyExists = true;
            return false;
        }
        catch (IOException ex)
        {
            errorCode = ExtractErrorCode(ex);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            errorCode = ErrorAccessDenied;
            return false;
        }
    }

    private static bool TryCreateFromData(
        ShellNewEntry entry,
        string candidateLong,
        out bool alreadyExists,
        out int errorCode)
    {
        alreadyExists = false;
        errorCode = 0;

        // Data 类型必须避免覆盖：先探测
        if (FileOperations.Exists(candidateLong))
        {
            alreadyExists = true;
            return false;
        }

        try
        {
            File.WriteAllBytes(candidateLong, entry.Data ?? Array.Empty<byte>());
            return true;
        }
        catch (IOException ex) when (IsAlreadyExists(ex))
        {
            alreadyExists = true;
            return false;
        }
        catch (IOException ex)
        {
            errorCode = ExtractErrorCode(ex);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            errorCode = ErrorAccessDenied;
            return false;
        }
    }

    private static string InsertOrdinal(string baseName, int ordinal)
    {
        string suffix = " (" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
        string extension = PathHelper.GetExtension(baseName);

        if (extension.Length == 0)
        {
            return baseName + suffix;
        }

        string stem = baseName.Substring(0, baseName.Length - extension.Length);
        return stem + suffix + extension;
    }

    private static bool IsAlreadyExists(IOException ex)
    {
        int code = ExtractErrorCode(ex);
        return code == ErrorAlreadyExists || code == ErrorFileExists;
    }

    private static int ExtractErrorCode(Exception ex)
    {
        int hr = ex.HResult;
        int code = hr & 0xFFFF;
        return code == 0 ? -1 : code;
    }

    private static string DescribeError(int errorCode)
    {
        if (errorCode > 0)
        {
            return Win32ErrorMessages.Describe(errorCode);
        }

        return "操作失败。";
    }

    private static string DescribeError(int errorCode, Exception ex)
    {
        if (errorCode > 0)
        {
            return Win32ErrorMessages.Describe(errorCode);
        }

        try
        {
            return new Win32Exception(ex.HResult).Message;
        }
        catch
        {
            return ex.Message;
        }
    }
}
