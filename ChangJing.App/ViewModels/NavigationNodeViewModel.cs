using System.Collections.ObjectModel;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ChangJing.App.Core.FileSystem;
using ChangJing.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CustomWin.Utils.Logs;

namespace ChangJing.App.ViewModels;

/// <summary>左侧导航树的节点类型</summary>
public enum NavigationNodeKind
{
    /// <summary>分组标题，不可导航</summary>
    Group,

    ThisPc,

    /// <summary>快速访问里的已知文件夹</summary>
    KnownFolder,

    Drive,

    /// <summary>驱动器 / 文件夹展开后列出的子文件夹</summary>
    Folder,

    RecycleBin,
}

/// <summary>左侧导航树的一个节点，展开时才在后台懒加载子文件夹</summary>
public sealed partial class NavigationNodeViewModel : ObservableObject
{
    private readonly Func<string, CancellationToken, SubfolderLoadResult>? _childLoader;
    private readonly Dispatcher _dispatcher;

    private bool _childrenLoaded;

    private NavigationNodeViewModel(NavigationNodeKind kind, string displayName, string? displayPath, string? longPath, ImageSource? icon, bool canExpand, Func<string, CancellationToken, SubfolderLoadResult>? childLoader)
    {
        Kind = kind;
        DisplayName = displayName;
        DisplayPath = displayPath;
        LongPath = longPath;
        Icon = icon;
        CanExpand = canExpand;
        _childLoader = childLoader;
        _dispatcher = Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;

        if (canExpand)
        {
            // 占位子节点，让 TreeViewItem 显示展开箭头
            Children.Add(Placeholder());
        }
    }

    public NavigationNodeKind Kind { get; }

    public string DisplayName { get; }

    /// <summary>导航目标路径；分组 / 回收站为 <c>null</c></summary>
    public string? DisplayPath { get; }

    /// <summary>扩展路径，枚举子文件夹用</summary>
    public string? LongPath { get; }

    /// <summary>图标；分组节点为 <c>null</c></summary>
    public ImageSource? Icon { get; }

    public bool CanExpand { get; }

    public bool IsPlaceholder { get; private set; }

    /// <summary>展开失败时的中文提示；成功为空串</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadFailure))]
    private string _loadFailureMessage = string.Empty;

    public bool HasLoadFailure => LoadFailureMessage.Length > 0;

    [ObservableProperty]
    private bool _isAccessDenied;

    [ObservableProperty]
    private bool _isUnavailable;

    public ObservableCollection<NavigationNodeViewModel> Children { get; } = new();

    [ObservableProperty]
    private bool _isLoadingChildren;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>是否展开；首次展开触发懒加载</summary>
    [ObservableProperty]
    private bool _isExpanded;

    public bool CanNavigate => DisplayPath is not null;

    public string ToolTip => DisplayPath ?? DisplayName;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            _ = EnsureChildrenLoadedAsync();
        }
    }

    // 工厂

    public static NavigationNodeViewModel CreateGroup(string name)
        => new(NavigationNodeKind.Group, name, null, null, null, canExpand: false, childLoader: null);

    public static NavigationNodeViewModel CreateContainer(
        NavigationNodeKind kind,
        string name,
        ImageSource? icon,
        string? displayPath = null)
    {
        return new NavigationNodeViewModel(kind, name, displayPath, null, icon, canExpand: true, childLoader: null)
        {
            IsExpanded = true,
        };
    }

    public static NavigationNodeViewModel CreateKnownFolder(KnownFolderItem folder)
        => new(
            NavigationNodeKind.KnownFolder,
            folder.DisplayName,
            folder.DisplayPath,
            folder.LongPath,
            SystemIconService.GetKnownFolderIcon(folder.Id),
            canExpand: false,
            childLoader: null);

    public static NavigationNodeViewModel CreateDrive(
        DriveItem drive,
        Func<string, CancellationToken, SubfolderLoadResult> childLoader)
        => new(
            NavigationNodeKind.Drive,
            drive.DisplayName,
            drive.IsReady ? drive.RootPath : null,
            drive.IsReady ? drive.RootPath : null,
            SystemIconService.GetDriveIcon(),
            canExpand: drive.IsReady,
            childLoader: childLoader);

    public static NavigationNodeViewModel CreateFolder(
        SubfolderItem folder,
        Func<string, CancellationToken, SubfolderLoadResult> childLoader)
        => new(
            NavigationNodeKind.Folder,
            folder.Name,
            folder.DisplayPath,
            folder.LongPath,
            SystemIconService.GetIcon(folder.Name, isDirectory: true),
            canExpand: true,
            childLoader: childLoader);

    public static NavigationNodeViewModel CreateRecycleBin()
        => new(
            NavigationNodeKind.RecycleBin,
            "回收站",
            null,
            null,
            SystemIconService.GetKnownFolderIcon("RecycleBin"),
            canExpand: false,
            childLoader: null);

    // 懒加载

    /// <summary>确保子节点已加载，可重复调用</summary>
    public async Task EnsureChildrenLoadedAsync()
    {
        if (_childrenLoaded || _childLoader is null || LongPath is null)
        {
            return;
        }

        _childrenLoaded = true;
        IsLoadingChildren = true;
        IReadOnlyList<SubfolderItem> folders = Array.Empty<SubfolderItem>();
        try
        {
            string longPath = LongPath;
            SubfolderLoadResult result = await Task
                .Run(() => _childLoader(longPath, CancellationToken.None))
                .ConfigureAwait(true);

            folders = result.Folders;
            ApplyLoadFailure(result);
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(NavigationNodeViewModel), $"展开节点失败（{LongPath}）：{ex.Message}");
            folders = Array.Empty<SubfolderItem>();
        }

        IsLoadingChildren = false;

        // 回到 UI 线程更新集合
        if (_dispatcher.CheckAccess())
        {
            ApplyChildren(folders);
        }
        else
        {
            _dispatcher.Invoke(() => ApplyChildren(folders));
        }
    }

    private void ApplyLoadFailure(SubfolderLoadResult result)
    {
        if (!result.Failed)
        {
            LoadFailureMessage = string.Empty;
            IsAccessDenied = false;
            IsUnavailable = false;
            return;
        }

        IsAccessDenied = result.AccessDenied;
        IsUnavailable = !result.AccessDenied;
        LoadFailureMessage = result.AccessDenied
            ? "你没有访问此文件夹的权限。"
            : "此位置不可用";
    }

    /// <summary>强制重新加载子节点，忽略已加载标记</summary>
    public async Task ReloadChildrenAsync()
    {
        if (_childLoader is null || LongPath is null)
        {
            return;
        }

        _childrenLoaded = false;
        await EnsureChildrenLoadedAsync().ConfigureAwait(true);
    }

    private void ApplyChildren(IReadOnlyList<SubfolderItem> folders)
    {
        Children.Clear();

        foreach (SubfolderItem folder in folders)
        {
            if (_childLoader is null)
            {
                break;
            }

            Children.Add(CreateFolder(folder, _childLoader));
        }

        if (Children.Count == 0)
        {
            // 展开后没有子文件夹，TreeView 会自动隐藏箭头
            _childrenLoaded = true;
        }
    }

    private static NavigationNodeViewModel Placeholder()
    {
        var node = new NavigationNodeViewModel(
            NavigationNodeKind.Folder,
            "正在加载…",
            null,
            null,
            null,
            canExpand: false,
            childLoader: null);
        node.IsPlaceholder = true;
        return node;
    }

    /// <inheritdoc/>
    public override string ToString() => DisplayName;
}
