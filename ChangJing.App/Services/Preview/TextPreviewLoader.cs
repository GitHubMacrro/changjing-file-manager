using System.Text;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Services.Preview;

/// <summary>文本预览加载器：后台读文件开头解码，失败返回空结果</summary>
public static class TextPreviewLoader
{
    public static Task<TextPreview> LoadAsync(string longPath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(longPath))
        {
            return Task.FromResult(TextPreview.Empty);
        }

        return Task.Run(() => Load(longPath, ct), ct);
    }

    private static TextPreview Load(string longPath, CancellationToken ct)
    {
        try
        {
            byte[]? data = LongPathFileReader.ReadBytes(longPath, PreviewTypeRules.TextPreviewByteLimit, out bool truncated, ct);
            if (data is null)
            {
                return TextPreview.Empty;
            }

            ct.ThrowIfCancellationRequested();

            string encodingName = LongPathFileReader.DetectEncodingName(data, data.Length, out Encoding encoding);
            string text = encoding.GetString(data, 0, data.Length);

            // 去掉编码器可能带出的开头 BOM 字符
            if (text.Length > 0 && text[0] == '\uFEFF')
            {
                text = text.Substring(1);
            }

            return new TextPreview
            {
                Text = text,
                Truncated = truncated,
                EncodingName = encodingName,
                BytesRead = data.Length,
            };
        }
        catch (OperationCanceledException)
        {
            return TextPreview.Empty;
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(TextPreviewLoader), $"文本预览失败（{longPath}）：{ex.Message}");
            return TextPreview.Empty;
        }
    }
}
