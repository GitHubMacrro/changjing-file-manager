using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ChangJing.App.Controls;

/// <summary>列表命中测试辅助，判断点击是否落在名称单元格内容上</summary>
// 名称模板打了标记，命中时从 OriginalSource 向上找，遇到 ListViewItem 即停，避免误判整行
public static class ListHitTest
{
    /// <summary>是否属于名称单元格内容</summary>
    public static readonly DependencyProperty IsNameContentProperty =
        DependencyProperty.RegisterAttached(
            "IsNameContent",
            typeof(bool),
            typeof(ListHitTest),
            new PropertyMetadata(false));

    /// <summary>读取标记</summary>
    public static bool GetIsNameContent(DependencyObject element) => (bool)element.GetValue(IsNameContentProperty);

    /// <summary>设置标记</summary>
    public static void SetIsNameContent(DependencyObject element, bool value) => element.SetValue(IsNameContentProperty, value);

    /// <summary>是否为行内的交互控件（复选框等），按下时不该算空白</summary>
    public static readonly DependencyProperty IsItemChromeProperty =
        DependencyProperty.RegisterAttached(
            "IsItemChrome",
            typeof(bool),
            typeof(ListHitTest),
            new PropertyMetadata(false));

    /// <summary>读取交互控件标记</summary>
    public static bool GetIsItemChrome(DependencyObject element) => (bool)element.GetValue(IsItemChromeProperty);

    /// <summary>设置交互控件标记</summary>
    public static void SetIsItemChrome(DependencyObject element, bool value) => element.SetValue(IsItemChromeProperty, value);

    /// <summary>命中元素是否落在名称单元格内容上，向上查找到 ListViewItem 为止</summary>
    public static bool IsNameContentHit(DependencyObject? source) => Walk(source, IsNameContentProperty);

    /// <summary>命中元素是否为行内交互控件，向上查找到 ListViewItem 为止</summary>
    public static bool IsItemChromeHit(DependencyObject? source) => Walk(source, IsItemChromeProperty);

    private static bool Walk(DependencyObject? source, DependencyProperty flag)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if ((bool)current.GetValue(flag))
            {
                return true;
            }

            if (current is ListViewItem)
            {
                return false;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
