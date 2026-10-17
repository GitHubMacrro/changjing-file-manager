namespace ChangJing.App.Services.Preview;

/// <summary>预览类型：无法预览 / 图片 / 文本 / PDF / 其他</summary>
public enum PreviewKind { None, Image, Text, Pdf, Other }

/// <summary>预览目标：由主窗口从列表行映射而来，不依赖界面类型</summary>
public sealed class PreviewTarget
{
    public required string Name { get; init; }
    public required string DisplayPath { get; init; }   // 普通路径，永不含 \\?\
    public required string LongPath { get; init; }      // 扩展路径
    public bool IsDirectory { get; init; }
    public bool IsDrive { get; init; }
    public ulong Size { get; init; }
    public DateTime LastWriteTime { get; init; }        // 本地时间；MinValue = 无
    public string TypeName { get; init; } = string.Empty;
    public System.Windows.Media.ImageSource? Icon48 { get; init; }
    public string SizeText { get; init; } = string.Empty;  // 已格式化的「1.5 MB」；空串表示无
    public string IssueReason { get; init; } = string.Empty; // 超长/特殊名称原因；空串表示正常
    public bool HasBadge { get; init; }
}

public sealed class TextPreview
{
    /// <summary>解码后的文本，可能只是文件开头的一部分</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>是否因超出上限而只读了前一段</summary>
    public bool Truncated { get; init; }

    /// <summary>检测到的编码名</summary>
    public string EncodingName { get; init; } = string.Empty;

    public long BytesRead { get; init; }

    /// <summary>空结果，也表示读取失败</summary>
    public static TextPreview Empty { get; } = new();
}

/// <summary>PDF 的静态信息：页数、首页尺寸</summary>
public sealed class PdfPreview
{
    public int PageCount { get; init; }

    /// <summary>首页宽度，单位 PDF 点，四舍五入</summary>
    public uint PageWidth { get; init; }

    /// <summary>首页高度，单位 PDF 点，四舍五入</summary>
    public uint PageHeight { get; init; }
}
