using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ChangJing.Regression;

/// <summary>把可视化元素渲染成 PNG；宽度超过上限时等比缩小</summary>
public static class ScreenshotCapture
{
    /// <summary>输出 PNG 的最大像素宽度（公开文档要求不超过 1600）</summary>
    public const int MaxPixelWidth = 1600;

    /// <summary>渲染 element 到 path；返回实际像素尺寸，失败返回 null</summary>
    public static (int Width, int Height)? Save(FrameworkElement element, string path, int maxWidth = MaxPixelWidth)
    {
        try
        {
            element.UpdateLayout();
            double dipWidth = element.ActualWidth;
            double dipHeight = element.ActualHeight;
            if (dipWidth <= 0 || dipHeight <= 0)
            {
                return null;
            }

            double scale = Math.Min(1.0, maxWidth / dipWidth);
            int pixelWidth = Math.Max(1, (int)Math.Round(dipWidth * scale));
            int pixelHeight = Math.Max(1, (int)Math.Round(dipHeight * scale));

            var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(element);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }

            return (pixelWidth, pixelHeight);
        }
        catch (Exception ex)
        {
            H.Info($"  [截图失败] {path}：{ex.Message}");
            return null;
        }
    }
}
