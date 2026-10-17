using System.IO;
using ChangJing.App.Core.LongPath;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Services.Preview;

/// <summary>预览处理器只吃路径时，长路径文件先复制到临时目录</summary>
internal static class PreviewTempCopy
{
    /// <summary>复制长路径文件到预览临时目录；失败返回 null</summary>
    public static string? Prepare(string longPath, string displayPath)
    {
        try
        {
            string directory = PdfPreviewLoader.PreviewTempDirectory;
            Directory.CreateDirectory(directory);

            string extension = PathHelper.GetExtension(displayPath);
            if (extension.Length > 12)
            {
                extension = string.Empty;
            }

            string destination = Path.Combine(directory, Guid.NewGuid().ToString("N") + extension);

            using (var source = new FileStream(
                       longPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                source.CopyTo(target);
            }

            return destination;
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(PreviewTempCopy), $"复制预览临时副本失败（{displayPath}）：{ex.Message}");
            return null;
        }
    }

    /// <summary>删除一个预览临时副本，失败只记日志</summary>
    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(PreviewTempCopy), $"删除预览临时副本失败（{path}）：{ex.Message}");
        }
    }
}
