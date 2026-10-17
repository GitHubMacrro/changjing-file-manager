namespace ChangJing.App.ViewModels;

/// <summary>文件列表的 8 种视图模式</summary>
public enum FileViewMode
{
    ExtraLargeIcons,

    LargeIcons,

    MediumIcons,

    SmallIcons,

    List,

    Details,

    Tiles,

    Content,
}

/// <summary>视图模式的元数据：名称、图标尺寸、快捷键序号、阈值映射</summary>
public static class FileViewModeInfo
{
    /// <summary>快捷键顺序，Ctrl+Shift+1..8</summary>
    public static readonly FileViewMode[] ShortcutOrder =
    {
        FileViewMode.ExtraLargeIcons,
        FileViewMode.LargeIcons,
        FileViewMode.MediumIcons,
        FileViewMode.SmallIcons,
        FileViewMode.List,
        FileViewMode.Details,
        FileViewMode.Tiles,
        FileViewMode.Content,
    };

    public static string GetName(FileViewMode mode) => mode switch
    {
        FileViewMode.ExtraLargeIcons => "超大图标",
        FileViewMode.LargeIcons => "大图标",
        FileViewMode.MediumIcons => "中等图标",
        FileViewMode.SmallIcons => "小图标",
        FileViewMode.List => "列表",
        FileViewMode.Details => "详细信息",
        FileViewMode.Tiles => "平铺",
        FileViewMode.Content => "内容",
        _ => "详细信息",
    };

    /// <summary>该视图的规范图标边长，单位像素</summary>
    public static int GetIconSize(FileViewMode mode) => mode switch
    {
        FileViewMode.ExtraLargeIcons => 256,
        FileViewMode.LargeIcons => 96,
        FileViewMode.MediumIcons => 48,
        FileViewMode.SmallIcons => 16,
        FileViewMode.Tiles => 48,
        _ => 16,
    };

    /// <summary>是否为可连续缩放的「图标」视图</summary>
    public static bool IsIconView(FileViewMode mode)
        => mode is FileViewMode.ExtraLargeIcons or FileViewMode.LargeIcons
            or FileViewMode.MediumIcons or FileViewMode.SmallIcons;

    /// <summary>按图标边长映射到最接近的图标视图</summary>
    public static FileViewMode FromIconSize(double size)
    {
        if (size <= 20)
        {
            return FileViewMode.SmallIcons;
        }

        if (size <= 64)
        {
            return FileViewMode.MediumIcons;
        }

        return size <= 160 ? FileViewMode.LargeIcons : FileViewMode.ExtraLargeIcons;
    }

    /// <summary>快捷键序号 1..8，未知返回 0</summary>
    public static int ToShortcutNumber(FileViewMode mode)
    {
        int index = Array.IndexOf(ShortcutOrder, mode);
        return index < 0 ? 0 : index + 1;
    }

    /// <summary>快捷键序号 1..8 对应的视图，越界返回 null</summary>
    public static FileViewMode? FromShortcutNumber(int number)
        => number is >= 1 and <= 8 ? ShortcutOrder[number - 1] : null;

    /// <summary>工具栏 / 右键菜单「查看」下拉里显示的视图名</summary>
    public static string GetMenuLabel(FileViewMode mode) => GetName(mode);
}
