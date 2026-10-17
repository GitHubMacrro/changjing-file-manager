using ChangJing.App.Core.Settings;

namespace ChangJing.App.Services.Search;

/// <summary>搜索框历史，最近使用可清除：把最近 10 条关键词存进设置</summary>
public sealed class SearchHistoryStore
{
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly object _gate = new();

    /// <summary>创建存取器，与主窗口共用同一份设置实例</summary>
    public SearchHistoryStore(SettingsService settingsService, AppSettings settings)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        _settings.SearchHistory ??= new List<string>();
    }

    /// <summary>当前历史，最新在前，只读快照</summary>
    public IReadOnlyList<string> Items
    {
        get
        {
            lock (_gate)
            {
                return _settings.SearchHistory.ToList();
            }
        }
    }

    /// <summary>记录一条搜索关键词，去空白去重置顶截断，变化才落盘</summary>
    public void Add(string? keyword)
    {
        string trimmed = keyword?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return;
        }

        bool changed;
        lock (_gate)
        {
            int existing = _settings.SearchHistory.FindIndex(
                item => string.Equals(item, trimmed, StringComparison.CurrentCultureIgnoreCase));

            changed = existing != 0;
            if (existing >= 0)
            {
                _settings.SearchHistory.RemoveAt(existing);
            }

            _settings.SearchHistory.Insert(0, trimmed);

            if (_settings.SearchHistory.Count > AppSettings.MaxSearchHistory)
            {
                _settings.SearchHistory.RemoveRange(
                    AppSettings.MaxSearchHistory,
                    _settings.SearchHistory.Count - AppSettings.MaxSearchHistory);
            }
        }

        if (changed)
        {
            _settingsService.Save(_settings);
        }
    }

    /// <summary>清空历史，变化才落盘</summary>
    public void Clear()
    {
        bool changed;
        lock (_gate)
        {
            changed = _settings.SearchHistory.Count > 0;
            _settings.SearchHistory.Clear();
        }

        if (changed)
        {
            _settingsService.Save(_settings);
        }
    }
}
