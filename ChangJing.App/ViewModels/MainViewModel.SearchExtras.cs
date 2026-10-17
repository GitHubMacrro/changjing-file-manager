using ChangJing.App.Core.FileSystem;
using ChangJing.App.Services.Search;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CustomWin.Utils.Logs;

namespace ChangJing.App.ViewModels;

/// <summary><see cref="MainViewModel"/> 的搜索增强部分：暂停 / 继续、搜索历史</summary>
public sealed partial class MainViewModel
{
    /// <summary>搜索暂停 / 继续闸门，每次搜索新建；取消 / 结束时释放</summary>
    private SearchPauseToken? _searchPause;

    /// <summary>搜索历史存取器，界面注入；为 <c>null</c> 时不记忆</summary>
    private SearchHistoryStore? _searchHistory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPauseSearch))]
    private bool _isSearchPaused;

    /// <summary>搜索结果名称列的高亮关键词，仅搜索结果视图非空</summary>
    public string SearchHighlightKeyword => IsSearchResultView ? _searchKeyword : string.Empty;

    /// <summary>是否可以暂停 / 继续，正在搜索时</summary>
    public bool CanPauseSearch => IsSearching;

    /// <summary>搜索历史的只读快照，界面下拉用</summary>
    public IReadOnlyList<string> SearchHistoryItems => _searchHistory?.Items ?? Array.Empty<string>();

    public bool HasSearchHistory => SearchHistoryItems.Count > 0;

    /// <summary>注入搜索历史存取器，主窗口构造后调用</summary>
    public void SetSearchHistory(SearchHistoryStore store)
    {
        _searchHistory = store;
        RaiseSearchHistoryChanged();
    }

    /// <summary>暂停 / 继续递归搜索，搜索期间按钮</summary>
    [RelayCommand(CanExecute = nameof(CanPauseSearch))]
    private void ToggleSearchPause()
    {
        SearchPauseToken? pause = _searchPause;
        if (pause is null)
        {
            return;
        }

        if (pause.IsPaused)
        {
            pause.Resume();
            IsSearchPaused = false;
        }
        else
        {
            pause.Pause();
            IsSearchPaused = true;
        }

        UpdateStatusText();
    }

    /// <summary>用历史里的一条关键词发起搜索，搜索历史下拉</summary>
    [RelayCommand]
    private void UseSearchHistory(string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return;
        }

        SearchText = keyword;
        if (SearchInSubfoldersCommand.CanExecute(null))
        {
            SearchInSubfoldersCommand.Execute(null);
        }
    }

    [RelayCommand]
    private void ClearSearchHistory()
    {
        _searchHistory?.Clear();
        RaiseSearchHistoryChanged();
    }

    private void RaiseSearchHistoryChanged()
    {
        OnPropertyChanged(nameof(SearchHistoryItems));
        OnPropertyChanged(nameof(HasSearchHistory));
    }

    partial void OnIsSearchingChanged(bool value)
    {
        ToggleSearchPauseCommand.NotifyCanExecuteChanged();
        if (!value)
        {
            IsSearchPaused = false;
        }
    }

    /// <summary>搜索开始时记录历史</summary>
    private void RecordSearchHistory(string keyword)
    {
        if (_searchHistory is null)
        {
            return;
        }

        _searchHistory.Add(keyword);
        RaiseSearchHistoryChanged();
    }
}
