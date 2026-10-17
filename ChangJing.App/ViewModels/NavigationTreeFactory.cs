using System.Collections.ObjectModel;
using System.Threading;
using ChangJing.App.Core.FileSystem;
using ChangJing.App.Services;

namespace ChangJing.App.ViewModels;

/// <summary>构建导航树根节点（「快速访问」+「此电脑」）的公共工厂，主窗口与「复制到…」选择器共用</summary>
public static class NavigationTreeFactory
{
    /// <summary>默认「此电脑」视图的显示名</summary>
    public const string ThisPcDisplayName = "此电脑";

    /// <summary>「此电脑」视图的内部标识</summary>
    public const string ThisPcToken = "::ThisPC";

    /// <summary>懒加载回调：枚举某个目录下的子文件夹，带回错误信息</summary>
    public static SubfolderLoadResult LoadChildFolders(
        string longPath,
        CancellationToken cancellationToken)
        => SubfolderProvider.Load(
            longPath,
            ExplorerSettings.ShowHiddenFiles,
            ExplorerSettings.ShowSystemFiles,
            cancellationToken);

    /// <summary>导航树根所需的系统信息快照（已知文件夹 + 驱动器）</summary>
    public readonly record struct NavigationSnapshot(
        IReadOnlyList<KnownFolderItem> KnownFolders,
        IReadOnlyList<DriveItem> Drives);

    /// <summary>探测导航树根所需的系统信息，可安全在后台线程调用</summary>
    public static NavigationSnapshot Probe()
        => new(LocationProvider.GetKnownFolders(), LocationProvider.GetDrives());

    /// <summary>填充一个导航树根集合：快速访问（六个已知文件夹）+ 此电脑（驱动器）</summary>
    public static void BuildRoots(ObservableCollection<NavigationNodeViewModel> roots, bool includeRecycleBin = true)
        => BuildRoots(roots, Probe(), includeRecycleBin);

    /// <summary>用预先探测好的快照填充导航树根集合</summary>
    public static void BuildRoots(
        ObservableCollection<NavigationNodeViewModel> roots,
        NavigationSnapshot snapshot,
        bool includeRecycleBin = true)
    {
        ArgumentNullException.ThrowIfNull(roots);

        roots.Clear();

        // 快速访问（六个已知文件夹，默认展开）
        NavigationNodeViewModel quickAccess = NavigationNodeViewModel.CreateContainer(
            NavigationNodeKind.Group, "快速访问", SystemIconService.GetIcon("folder", isDirectory: true));
        quickAccess.Children.Clear();
        foreach (KnownFolderItem folder in snapshot.KnownFolders)
        {
            quickAccess.Children.Add(NavigationNodeViewModel.CreateKnownFolder(folder));
        }

        roots.Add(quickAccess);

        // 此电脑（子节点为驱动器，可再展开懒加载）
        NavigationNodeViewModel thisPc = NavigationNodeViewModel.CreateContainer(
            NavigationNodeKind.ThisPc,
            ThisPcDisplayName,
            SystemIconService.GetDriveIcon(),
            ThisPcToken);
        thisPc.Children.Clear();
        foreach (DriveItem drive in snapshot.Drives)
        {
            thisPc.Children.Add(NavigationNodeViewModel.CreateDrive(drive, LoadChildFolders));
        }

        roots.Add(thisPc);

        if (includeRecycleBin)
        {
            // 回收站
            roots.Add(NavigationNodeViewModel.CreateRecycleBin());
        }
    }
}
