using ChangJing.App.Core.LongPath;
using ChangJing.App.Core.Settings;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Services;

/// <summary>「复制到…/移动到…」的最近使用目标位置，最多 10 个最新的在前</summary>
// 直接读写 SettingsService.Current 的 RecentTargets，每次变更后落盘
public sealed class RecentTargetStore
{
    private readonly SettingsService _settings;

    public RecentTargetStore(SettingsService settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>当前「最近使用」目标位置，普通路径，最新的在前</summary>
    public IReadOnlyList<string> Items => _settings.Current.RecentTargets;

    /// <summary>记入最近使用目标位置：去重、置顶、截断到上限并落盘</summary>
    public void Add(string displayPath)
    {
        if (string.IsNullOrWhiteSpace(displayPath))
        {
            return;
        }

        string normalized = Normalize(displayPath);
        List<string> targets = _settings.Current.RecentTargets ??= new List<string>();

        targets.RemoveAll(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase));
        targets.Insert(0, normalized);

        if (targets.Count > AppSettings.MaxRecentTargets)
        {
            targets.RemoveRange(AppSettings.MaxRecentTargets, targets.Count - AppSettings.MaxRecentTargets);
        }

        if (!_settings.Save())
        {
            LogManager.Warning(typeof(RecentTargetStore), $"保存最近使用目标位置失败：{normalized}");
        }
        else
        {
            LogManager.Info(typeof(RecentTargetStore), $"已记入最近使用目标位置：{normalized}");
        }
    }

    // 去掉多余 / 末尾分隔符；无法规范化时原样返回
    private static string Normalize(string displayPath)
    {
        try
        {
            return PathHelper.ToDisplay(PathHelper.Normalize(displayPath));
        }
        catch
        {
            return displayPath.Trim();
        }
    }
}
