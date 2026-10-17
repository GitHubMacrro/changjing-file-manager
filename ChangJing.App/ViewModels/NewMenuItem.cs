using ChangJing.App.Core.ShellNew;

namespace ChangJing.App.ViewModels;

/// <summary>「新建▸」菜单里的一项：文件夹、快捷方式、注册表 ShellNew 类型，分隔线用 <see cref="IsSeparator"/> 表示</summary>
public sealed class NewMenuItem
{
    private NewMenuItem(NewMenuItemKind kind)
    {
        Kind = kind;
    }

    public NewMenuItemKind Kind { get; }

    public string DisplayName { get; private init; } = string.Empty;

    /// <summary>ShellNew 条目，仅 ShellNew 类型有值</summary>
    public ShellNewEntry? Entry { get; private init; }

    /// <summary>菜单项图标；可能为 <c>null</c></summary>
    public System.Windows.Media.ImageSource? Icon { get; private set; }

    public bool IsSeparator => Kind == NewMenuItemKind.Separator;

    public static NewMenuItem Folder() => new(NewMenuItemKind.Folder)
    {
        DisplayName = "文件夹",
    };

    public static NewMenuItem Shortcut() => new(NewMenuItemKind.Shortcut)
    {
        DisplayName = "快捷方式",
    };

    public static NewMenuItem Separator() => new(NewMenuItemKind.Separator);

    public static NewMenuItem FromShellNew(ShellNewEntry entry)
    {
        var item = new NewMenuItem(NewMenuItemKind.ShellNew)
        {
            DisplayName = entry.DisplayName,
            Entry = entry,
        };

        try
        {
            item.Icon = Services.SystemIconService.GetIcon(entry.DefaultFileName, isDirectory: false);
        }
        catch
        {
            item.Icon = null;
        }

        return item;
    }
}

/// <summary>「新建▸」菜单项的种类</summary>
public enum NewMenuItemKind
{
    Folder,

    /// <summary>快捷方式，.lnk</summary>
    Shortcut,

    /// <summary>注册表 ShellNew 登记的文件类型</summary>
    ShellNew,

    Separator,
}
