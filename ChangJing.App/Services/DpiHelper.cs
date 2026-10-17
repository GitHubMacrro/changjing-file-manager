using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace ChangJing.App.Services;

/// <summary>高 DPI 辅助：DIP 与物理像素换算</summary>
// 请求图标尺寸须乘缩放比，否则高 DPI 下发虚
public static class DpiHelper
{
    /// <summary>单值覆盖，仅验证用：> 0 时 SystemScale 直接返回它</summary>
    public static double? ScaleOverride { get; set; }

    /// <summary>当前系统主显示器 DPI 缩放比，1.0 = 96 DPI</summary>
    public static double SystemScale
    {
        get
        {
            if (ScaleOverride is { } over && over > 0)
            {
                return over;
            }

            try
            {
                int dpi = GetDpiForSystem();
                if (dpi <= 0)
                {
                    return 1.0;
                }

                return dpi / 96.0;
            }
            catch
            {
                return 1.0;
            }
        }
    }

    /// <summary>把 DIP 尺寸换算成物理像素，四舍五入，最小 1</summary>
    public static int PhysicalPixels(double dipSize, double scale = 0)
    {
        if (scale <= 0)
        {
            scale = SystemScale;
        }

        if (double.IsNaN(dipSize) || dipSize <= 0)
        {
            return 0;
        }

        return Math.Max(1, (int)Math.Round(dipSize * scale));
    }

    /// <summary>取可视元素所在显示器的缩放比，不在树中时回退 SystemScale</summary>
    public static double ScaleFor(Visual? visual)
    {
        if (visual is null)
        {
            return SystemScale;
        }

        try
        {
            double scale = VisualTreeHelper.GetDpi(visual).DpiScaleX;
            return scale > 0 ? scale : SystemScale;
        }
        catch
        {
            return SystemScale;
        }
    }

    /// <summary>把细线线宽换算成对齐设备像素的 DIP 值，避免模糊灰线</summary>
    public static double HairlineThickness(double dipThickness = 1.0, double scale = 0)
    {
        if (scale <= 0)
        {
            scale = SystemScale;
        }

        if (scale <= 0)
        {
            scale = 1.0;
        }

        double physical = Math.Max(1.0, Math.Round(dipThickness * scale));
        return physical / scale;
    }

    [DllImport("user32.dll")]
    private static extern int GetDpiForSystem();
}
