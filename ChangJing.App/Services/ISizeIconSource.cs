using System.Windows.Media;

namespace ChangJing.App.Services;

/// <summary>能按目标像素尺寸提供系统图标的对象</summary>
public interface ISizeIconSource
{
    /// <summary>取指定边长的系统图标，已 Freeze，取不到返回 null</summary>
    ImageSource? GetIconForSize(int size);
}
