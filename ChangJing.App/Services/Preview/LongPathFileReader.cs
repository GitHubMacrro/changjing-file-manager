using System.IO;
using System.Text;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Services.Preview;

/// <summary>长路径文件读取助手：用扩展路径打开文件，只读、不锁文件，读完立即关闭</summary>
public static class LongPathFileReader
{
    static LongPathFileReader()
    {
        // .NET Core 默认不带 GBK 等代码页，注册一次 CodePages 提供程序
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(LongPathFileReader), $"注册代码页提供程序失败：{ex.Message}");
        }
    }

    /// <summary>读取文件的前 maxBytes 个字节，更长时 truncated 为 true，失败返回 null</summary>
    public static byte[]? ReadBytes(string longPath, int maxBytes, out bool truncated, CancellationToken ct)
    {
        truncated = false;

        if (string.IsNullOrEmpty(longPath) || maxBytes <= 0)
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(
                longPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            long length = stream.Length;
            int toRead = (int)Math.Min(length, maxBytes);
            truncated = length > maxBytes;

            var buffer = new byte[Math.Max(toRead, 1)];
            int total = 0;

            while (total < toRead)
            {
                ct.ThrowIfCancellationRequested();
                int read = stream.Read(buffer, total, toRead - total);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            if (total == buffer.Length)
            {
                return buffer;
            }

            var result = new byte[total];
            Array.Copy(buffer, result, total);
            return result;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(LongPathFileReader), $"读取文件失败（{longPath}）：{ex.Message}");
            return null;
        }
    }

    /// <summary>检测编码并返回中文编码名，识别 BOM / UTF-8 / GBK / ANSI</summary>
    public static string DetectEncodingName(byte[] data, int length, out Encoding encoding)
    {
        encoding = Encoding.UTF8;

        if (data is null || length <= 0)
        {
            return "UTF-8";
        }

        if (length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            return "UTF-8";
        }

        if (length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
        {
            encoding = Encoding.Unicode; // UTF-16 LE
            return "UTF-16 LE";
        }

        if (length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
        {
            encoding = Encoding.BigEndianUnicode; // UTF-16 BE
            return "UTF-16 BE";
        }

        // 无 BOM：按严格 UTF-8 试解码，不合法就往下走
        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            _ = strict.GetString(data, 0, length);
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            return "UTF-8";
        }
        catch (DecoderFallbackException)
        {
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(LongPathFileReader), $"UTF-8 校验失败：{ex.Message}");
        }

        try
        {
            encoding = Encoding.GetEncoding(936);
            return "GBK";
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(LongPathFileReader), $"取 GBK 编码失败，回退 ANSI：{ex.Message}");
        }

        encoding = Encoding.Latin1;
        return "ANSI";
    }
}
