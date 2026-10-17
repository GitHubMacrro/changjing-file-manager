using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Services.Preview;

/// <summary>图片预览加载器：后台用 WIC 按宽度解码，失败返回 null</summary>
public static class ImagePreviewLoader
{
    public static Task<ImageSource?> LoadAsync(string longPath, int decodePixelWidth, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(longPath))
        {
            return Task.FromResult<ImageSource?>(null);
        }

        return Task.Run(() => Load(longPath, decodePixelWidth, ct), ct);
    }

    private static ImageSource? Load(string longPath, int decodePixelWidth, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            using var stream = new FileStream(
                longPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.DecodePixelWidth = Math.Max(32, decodePixelWidth);
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(ImagePreviewLoader), $"图片预览解码失败（{longPath}）：{ex.Message}");
            return null;
        }
    }
}
