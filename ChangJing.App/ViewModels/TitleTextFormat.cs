namespace ChangJing.App.ViewModels;

/// <summary>标题栏文字格式 <c>畅径[（管理员）] - &lt;位置名&gt;</c>；Window.Title 完整，仅标题栏显示省略</summary>
public static class TitleTextFormat
{
    /// <summary>应用名，与 <c>MainViewModel.ApplicationTitle</c> 一致</summary>
    public const string ApplicationName = "畅径";

    /// <summary>应用名与位置名之间的分隔符</summary>
    public const string Separator = " - ";

    /// <summary>管理员后缀的标签</summary>
    public const string AdministratorLabel = "管理员";

    /// <summary>应用名标签：「畅径」/「畅径（管理员）」</summary>
    public static string Brand(bool elevated)
        => elevated
            ? $"{ApplicationName}（{AdministratorLabel}）"
            : ApplicationName;

    /// <summary>完整标题：有位置时「畅径[（管理员）] - &lt;位置名&gt;」</summary>
    public static string Compose(string? locationName, bool elevated)
    {
        string brand = Brand(elevated);

        if (string.IsNullOrEmpty(locationName))
        {
            return brand;
        }

        return $"{brand}{Separator}{locationName}";
    }

    /// <summary>搜索结果视图的位置名：「搜索“关键词”」，去掉首尾空白</summary>
    public static string SearchLocationName(string? keyword)
        => $"搜索“{(keyword ?? string.Empty).Trim()}”";

    /// <summary>标题栏「主文字」与「后缀」；无位置时后缀为空串</summary>
    public static (string Main, string Suffix) Split(string? locationName, bool elevated)
    {
        string brand = Brand(elevated);

        if (string.IsNullOrEmpty(locationName))
        {
            return (brand, string.Empty);
        }

        return (brand, $"{Separator}{locationName}");
    }

    /// <summary>是否为「盘符根」，如 <c>C:\</c></summary>
    public static bool IsDriveRoot(string? location)
        => location is { Length: 3 }
           && char.IsLetter(location[0])
           && location[1] == ':'
           && location[2] == '\\';
}
