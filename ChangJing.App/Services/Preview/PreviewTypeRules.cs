using ChangJing.App.Core.LongPath;
using ChangJing.App.Services;

namespace ChangJing.App.Services.Preview;

/// <summary>预览类型判定：按扩展名归类，纯字符串不访问磁盘</summary>
public static class PreviewTypeRules
{
    /// <summary>文本预览最多读取的字节数，1 MB</summary>
    public const int TextPreviewByteLimit = 1024 * 1024;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".md", ".json", ".xml", ".csv", ".ini", ".cfg", ".conf",
        ".yml", ".yaml", ".toml",
        ".cs", ".py", ".js", ".ts", ".tsx", ".jsx", ".java", ".c", ".h", ".cpp", ".hpp",
        ".css", ".html", ".htm", ".bat", ".cmd", ".ps1", ".sh", ".sql",
        ".gitignore", ".editorconfig",
    };

    private static readonly HashSet<string> TextFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "README", "LICENSE", "Makefile", "Dockerfile",
        ".gitignore", ".editorconfig", "settings.json",
    };

    private static readonly HashSet<string> PdfExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf",
    };

    /// <summary>判定一个条目的预览类型，文件夹 / 驱动器一律归为 Other</summary>
    public static PreviewKind Classify(string name, bool isDirectory)
    {
        if (isDirectory)
        {
            return PreviewKind.Other;
        }

        string extension = PathHelper.GetExtension(name);
        if (extension.Length > 0)
        {
            if (IsImageExtension(extension))
            {
                return PreviewKind.Image;
            }

            if (IsPdfExtension(extension))
            {
                return PreviewKind.Pdf;
            }

            if (IsTextExtension(extension))
            {
                return PreviewKind.Text;
            }

            return PreviewKind.Other;
        }

        // 无扩展名：少数常见文本文件名按文本预览
        return TextFileNames.Contains(name) ? PreviewKind.Text : PreviewKind.Other;
    }

    /// <summary>扩展名含点，是否为文本类型</summary>
    public static bool IsTextExtension(string extension)
        => !string.IsNullOrEmpty(extension) && TextExtensions.Contains(extension);

    /// <summary>扩展名含点，是否为 PDF</summary>
    public static bool IsPdfExtension(string extension)
        => !string.IsNullOrEmpty(extension) && PdfExtensions.Contains(extension);

    /// <summary>扩展名含点，是否为可显示图片</summary>
    public static bool IsImageExtension(string extension)
        => ThumbnailService.IsImageExtension(extension);
}
