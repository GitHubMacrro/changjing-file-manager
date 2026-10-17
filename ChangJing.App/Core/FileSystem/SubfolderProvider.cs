using ChangJing.App.Core.LongPath;
using ChangJing.App.Services;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Core.FileSystem;

/// <summary>一个子文件夹，用于地址栏面包屑下拉与导航树懒加载</summary>
public sealed class SubfolderItem
{
    public SubfolderItem(string name, string displayPath, string longPath, bool isHidden, bool isSystem)
    {
        Name = name;
        DisplayPath = displayPath;
        LongPath = longPath;
        IsHidden = isHidden;
        IsSystem = isSystem;
    }

    /// <summary>文件夹名，不含路径，保留结尾点与空格</summary>
    public string Name { get; }

    /// <summary>界面显示用的完整普通路径，永不含 \\?\</summary>
    public string DisplayPath { get; }

    /// <summary>文件 API 用的完整扩展路径</summary>
    public string LongPath { get; }

    public bool IsHidden { get; }

    public bool IsSystem { get; }
}

/// <summary>子文件夹枚举结果，带回错误信息</summary>
public sealed class SubfolderLoadResult
{
    /// <summary>枚举到的子文件夹，失败时为空列表</summary>
    public IReadOnlyList<SubfolderItem> Folders { get; init; } = Array.Empty<SubfolderItem>();

    /// <summary>Win32 错误码，成功为 0</summary>
    public int ErrorCode { get; init; }

    /// <summary>失败原因，中文；成功时为空串</summary>
    public string Error { get; init; } = string.Empty;

    public bool AccessDenied => ErrorCode == Win32ErrorMessages.ErrorAccessDenied;

    public bool Failed => ErrorCode != 0;
}

/// <summary>只列出某个文件夹下的子文件夹，不列文件</summary>
public static class SubfolderProvider
{
    /// <summary>枚举路径下的子文件夹，按名称自然排序；失败时为空列表</summary>
    public static IReadOnlyList<SubfolderItem> GetSubfolders(
        string path,
        bool includeHidden,
        bool includeSystem,
        CancellationToken cancellationToken = default)
        => Load(path, includeHidden, includeSystem, cancellationToken).Folders;

    /// <summary>同 GetSubfolders，但把成功 / 错误码 / 中文原因一并返回</summary>
    public static SubfolderLoadResult Load(
        string path,
        bool includeHidden,
        bool includeSystem,
        CancellationToken cancellationToken = default)
    {
        var folders = new List<SubfolderItem>();

        if (string.IsNullOrWhiteSpace(path))
        {
            return new SubfolderLoadResult { Folders = folders };
        }

        try
        {
            string longPath = PathHelper.ToLong(path);
            var enumerator = new DirectoryEnumerator(batchSize: 500, skipHiddenAndSystem: false);

            DirectoryEnumerationResult result = enumerator.Enumerate(
                longPath,
                batch =>
                {
                    foreach (FileEntry entry in batch)
                    {
                        if (!entry.IsDirectory)
                        {
                            continue;
                        }

                        if (entry.IsSystem && !includeSystem)
                        {
                            continue;
                        }

                        if (entry.IsHidden && !includeHidden)
                        {
                            continue;
                        }

                        folders.Add(new SubfolderItem(
                            entry.Name,
                            entry.DisplayPath,
                            entry.FullPath,
                            entry.IsHidden,
                            entry.IsSystem));
                    }
                },
                cancellationToken);

            if (result.Canceled)
            {
                return new SubfolderLoadResult { Folders = Array.Empty<SubfolderItem>() };
            }

            if (!result.Success)
            {
                return new SubfolderLoadResult
                {
                    Folders = Array.Empty<SubfolderItem>(),
                    ErrorCode = result.ErrorCode,
                    Error = result.Error,
                };
            }

            folders.Sort(static (a, b) => NaturalComparer.Compare(a.Name, b.Name));
            return new SubfolderLoadResult { Folders = folders };
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(SubfolderProvider), $"列子文件夹失败（{path}）：{ex.Message}");
            return new SubfolderLoadResult
            {
                Folders = Array.Empty<SubfolderItem>(),
                ErrorCode = -1,
                Error = ex.Message,
            };
        }
    }
}
