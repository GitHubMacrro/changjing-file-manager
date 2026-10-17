using System.Collections.ObjectModel;
using System.Windows.Media;
using ChangJing.App.Controls;
using ChangJing.App.Core.FileSystem;
using ChangJing.App.Core.LongPath;
using ChangJing.App.Core.Operations;
using ChangJing.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CustomWin.Utils.Logs;

namespace ChangJing.App.ViewModels;

/// <summary>文件夹浏览器里的一行条目</summary>
public sealed class FolderBrowserEntry
{
    public FolderBrowserEntry(string name, string displayPath, string longPath, bool isParent, ImageSource? icon)
    {
        Name = name;
        DisplayPath = displayPath;
        LongPath = longPath;
        IsParent = isParent;
        Icon = icon;
    }

    public string Name { get; }

    /// <summary>完整普通路径</summary>
    public string DisplayPath { get; }

    /// <summary>完整扩展路径</summary>
    public string LongPath { get; }

    public bool IsParent { get; }

    public ImageSource? Icon { get; }

    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>文件夹浏览器：逐层浏览、单击选中，确认按钮才提交</summary>
public sealed partial class FolderPickerViewModel : ObservableObject
{
    /// <summary>「此电脑」层：地址栏显示的内部标识</summary>
    public const string ThisPcToken = NavigationTreeFactory.ThisPcToken;

    public const string ThisPcName = NavigationTreeFactory.ThisPcDisplayName;

    public FolderPickerViewModel()
    {
    }

    public ObservableCollection<FolderBrowserEntry> Entries { get; } = new();

    /// <summary>面包屑段；最后一段为当前位置</summary>
    [ObservableProperty]
    private IReadOnlyList<BreadcrumbSegment> _breadcrumbSegments = Array.Empty<BreadcrumbSegment>();

    /// <summary>当前目录的完整普通路径</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoUp))]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private string _currentPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private FolderBrowserEntry? _selectedEntry;

    /// <summary>路径解析 / 定位失败时的中文提示</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPathError))]
    private string _pathError = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public bool HasPathError => PathError.Length > 0;

    public bool IsThisPcLevel => string.Equals(CurrentPath, ThisPcToken, StringComparison.Ordinal);

    public bool CanGoUp => !IsThisPcLevel && !string.IsNullOrEmpty(CurrentPath);

    public bool CanConfirm
    {
        get
        {
            if (IsThisPcLevel)
            {
                return false;
            }

            if (SelectedEntry is { IsParent: false } entry)
            {
                return IsExistingDirectory(entry.DisplayPath);
            }

            return IsExistingDirectory(CurrentPath);
        }
    }

    partial void OnCurrentPathChanged(string value)
    {
        OnPropertyChanged(nameof(IsThisPcLevel));
        PathError = string.Empty;
    }

    partial void OnSelectedEntryChanged(FolderBrowserEntry? value) => OnPropertyChanged(nameof(CanConfirm));

    /// <summary>确认后的目标目录普通路径</summary>
    public string? ResultPath { get; private set; }

    // 加载 / 导航

    public Task<bool> LoadAsync(string displayPath)
    {
        if (string.IsNullOrWhiteSpace(displayPath))
        {
            return Task.FromResult(false);
        }

        CurrentPath = displayPath;
        return RefreshAsync();
    }

    /// <inheritdoc cref="LoadAsync"/>
    public Task<bool> CommitPathAndLocateAsync(string displayPath) => LoadAsync(displayPath);

    public async Task<bool> CommitPathAsync()
    {
        string text = CurrentPath;

        if (string.Equals(text, ThisPcName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, ThisPcToken, StringComparison.Ordinal))
        {
            await LoadThisPcAsync().ConfigureAwait(true);
            return true;
        }

        if (!PathHelper.TryParseUserInput(text, LocationProvider.ResolveAlias, out PathParseResult parsed))
        {
            PathError = parsed.Error;
            return false;
        }

        string target = parsed.DisplayPath;
        if (!IsExistingDirectory(target))
        {
            PathError = "该位置不存在或不是文件夹。";
            return false;
        }

        CurrentPath = target;
        return await RefreshAsync().ConfigureAwait(true);
    }

    public async Task<bool> RefreshAsync()
    {
        if (IsThisPcLevel)
        {
            await LoadThisPcAsync().ConfigureAwait(true);
            return true;
        }

        if (!IsExistingDirectory(CurrentPath))
        {
            PathError = "该位置不存在或不是文件夹。";
            return false;
        }

        string path = CurrentPath;
        IsBusy = true;
        try
        {
            SubfolderLoadResult result = await Task.Run(() =>
                NavigationTreeFactory.LoadChildFolders(PathHelper.ToLong(path), CancellationToken.None))
                .ConfigureAwait(true);

            if (result.Failed)
            {
                PathError = result.AccessDenied ? "你没有访问此文件夹的权限。" : "此位置不可用";
                return false;
            }

            FillEntries(path, result.Folders);
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(FolderPickerViewModel), ex);
            PathError = "读取该文件夹时发生错误，详细信息已写入日志。";
            return false;
        }
        finally
        {
            IsBusy = false;
        }

        PathError = string.Empty;
        return true;
    }

    public async Task LoadThisPcAsync()
    {
        IsBusy = true;
        try
        {
            IReadOnlyList<DriveItem> drives = await Task.Run(() => LocationProvider.GetDrives()).ConfigureAwait(true);

            CurrentPath = ThisPcToken;
            SelectedEntry = null;
            BreadcrumbSegments = BuildBreadcrumbs(ThisPcToken);

            Entries.Clear();
            foreach (DriveItem drive in drives)
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                Entries.Add(new FolderBrowserEntry(
                    drive.DisplayName,
                    drive.RootPath,
                    drive.RootPath,
                    isParent: false,
                    SystemIconService.GetDriveIcon()));
            }

            PathError = string.Empty;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ActivateEntryAsync(FolderBrowserEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        if (entry.IsParent)
        {
            await GoUpAsync().ConfigureAwait(true);
            return;
        }

        await LoadAsync(entry.DisplayPath).ConfigureAwait(true);
    }

    public async Task GoUpAsync()
    {
        if (IsThisPcLevel)
        {
            return;
        }

        string? parent = null;
        try
        {
            parent = PathHelper.GetParent(PathHelper.ToLong(CurrentPath));
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(FolderPickerViewModel), $"取上一级失败：{ex.Message}");
        }

        if (parent is null)
        {
            await LoadThisPcAsync().ConfigureAwait(true);
        }
        else
        {
            await LoadAsync(PathHelper.ToDisplay(parent)).ConfigureAwait(true);
        }
    }

    public string? GetTargetPath()
    {
        if (SelectedEntry is { IsParent: false } entry && IsExistingDirectory(entry.DisplayPath))
        {
            return entry.DisplayPath;
        }

        return CanConfirm ? CurrentPath : null;
    }

    /// <summary>在当前目录下新建文件夹，随后刷新并选中新文件夹</summary>
    public async Task<bool> CreateFolderAsync()
    {
        if (IsThisPcLevel || !IsExistingDirectory(CurrentPath))
        {
            PathError = "当前位置不可用，无法新建文件夹。";
            return false;
        }

        string parent = CurrentPath;
        NameValidationResult validation = NameValidator.Validate(FileOperations.DefaultFolderName, parent);
        if (validation.HasError)
        {
            PathError = validation.Error;
            return false;
        }

        FileOperationResult created = FileOperations.CreateDirectory(parent, FileOperations.DefaultFolderName);
        if (created.IsFailed)
        {
            PathError = created.Error;
            return false;
        }

        PathError = string.Empty;
        IsBusy = true;
        try
        {
            await RefreshAsync().ConfigureAwait(true);

            FolderBrowserEntry? match = Entries.FirstOrDefault(
                e => string.Equals(e.DisplayPath, created.DisplayPath, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                SelectedEntry = match;
            }
        }
        finally
        {
            IsBusy = false;
        }

        return true;
    }

    public string? Confirm()
    {
        string? target = GetTargetPath();
        if (target is null)
        {
            return null;
        }

        ResultPath = target;
        return ResultPath;
    }

    public static bool IsExistingDirectory(string? displayPath)
    {
        if (string.IsNullOrWhiteSpace(displayPath) || string.Equals(displayPath, ThisPcToken, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            string longPath = PathHelper.ToLong(displayPath!);
            return FileOperations.TryGetEntryInfo(longPath, out uint attributes, out _, out _)
                   && (attributes & NativeMethods.FileAttributeDirectory) != 0;
        }
        catch
        {
            return false;
        }
    }

    // 内部

    private void FillEntries(string path, IReadOnlyList<SubfolderItem> folders)
    {
        SelectedEntry = null;
        Entries.Clear();

        BreadcrumbSegments = BuildBreadcrumbs(path);

        // 「返回上一级」：盘根时目标为「此电脑」
        string? parent = null;
        try
        {
            parent = PathHelper.GetParent(PathHelper.ToLong(path));
            parent = parent is null ? null : PathHelper.ToDisplay(parent);
        }
        catch
        {
            parent = null;
        }

        Entries.Add(new FolderBrowserEntry(
            "..",
            parent ?? ThisPcToken,
            parent is null ? ThisPcToken : PathHelper.ToLong(parent),
            isParent: true,
            null));

        foreach (SubfolderItem folder in folders)
        {
            Entries.Add(new FolderBrowserEntry(
                folder.Name,
                folder.DisplayPath,
                folder.LongPath,
                isParent: false,
                SystemIconService.GetIcon(folder.Name, isDirectory: true)));
        }
    }

    private static IReadOnlyList<BreadcrumbSegment> BuildBreadcrumbs(string path)
        => Breadcrumbs.Build(
            string.Equals(path, ThisPcToken, StringComparison.Ordinal) ? ThisPcToken : path,
            ThisPcName,
            ThisPcToken);
}
