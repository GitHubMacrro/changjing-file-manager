using ChangJing.App.Core.Settings;

namespace ChangJing.App.Services;

/// <summary>应用级服务容器，手工组装，无 DI 框架</summary>
public sealed class AppServices
{
    public AppServices(SettingsService settingsService, ThemeService themeService)
    {
        SettingsService = settingsService;
        ThemeService = themeService;
    }

    /// <summary>设置读写服务</summary>
    public SettingsService SettingsService { get; }

    /// <summary>主题与背景服务</summary>
    public ThemeService ThemeService { get; }
}
