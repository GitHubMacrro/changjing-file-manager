using System.Windows.Media;
using ChangJing.App.Core.FileSystem;
using ChangJing.App.Core.LongPath;
using ChangJing.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChangJing.App.ViewModels;

/// <summary>文件列表里的一行：图标、名称、时间、大小与重命名状态</summary>
public sealed partial class FileItemViewModel : ObservableObject, ISizeIconSource
{
    private FileEntry? _entry;

    public FileItemViewModel(FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _entry = entry;
        Name = entry.Name;
        DisplayPath = entry.DisplayPath;
        LongPath = entry.FullPath;
        IsDirectory = entry.IsDirectory;
        IsHidden = entry.IsHidden;
        IsSystem = entry.IsSystem;
        LastWriteTime = entry.LastWriteTime;
        SizeBytes = entry.Size;
        TypeName = SystemIconService.GetTypeName(entry.Name, entry.IsDirectory);
        SizeText = entry.IsDirectory ? string.Empty : FormatSize(entry.Size);
        Icon = SystemIconService.GetIcon(entry.Name, entry.IsDirectory);
        IsImage = !entry.IsDirectory && ThumbnailService.IsImageExtension(PathHelper.GetExtension(entry.Name));

        LongPathAnalysis analysis = LongPathRules.Analyze(DisplayPath);
        IsProblematic = analysis.IsProblematic;
        IssueReason = analysis.Reason;
        HasBadge = LongPathRules.ShouldShowBadge(DisplayPath);
    }

    /// <summary>由驱动器信息创建，用于「此电脑」视图</summary>
    public FileItemViewModel(DriveItem drive)
    {
        ArgumentNullException.ThrowIfNull(drive);

        Drive = drive;
        Name = drive.DisplayName;
        DisplayPath = drive.RootPath;
        LongPath = drive.RootPath;
        IsDirectory = true;
        IsHidden = false;
        IsSystem = false;
        LastWriteTime = DateTime.MinValue;
        SizeBytes = drive.TotalSizeBytes;
        TypeName = drive.IsReady ? drive.KindText : $"{drive.KindText}（未就绪）";
        SizeText = drive.HasSizeInfo
            ? $"{FormatSize(drive.FreeSpaceBytes)} 可用，共 {FormatSize(drive.TotalSizeBytes)}"
            : string.Empty;
        Icon = SystemIconService.GetDriveIcon();
        IsImage = false;
        IsProblematic = false;
        IssueReason = string.Empty;
        HasBadge = false;
    }

    // 按尺寸取图标

    public ImageSource? GetIconForSize(int size)
        => IsDrive
            ? SystemIconService.GetDriveIcon(size)
            : SystemIconService.GetIconForSize(Name, IsDirectory, size);

    /// <summary>内容视图 32px 图标</summary>
    public ImageSource? IconForContent => GetIconForSize(32);

    // 只读数据

    /// <summary>文件 / 文件夹名，驱动器为显示名</summary>
    [ObservableProperty]
    private string _name;

    /// <summary>完整普通路径，界面用，永不含 \\?\</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTooLong))]
    private string _displayPath;

    /// <summary>完整路径，扩展形态，供文件 API / 导航用</summary>
    [ObservableProperty]
    private string _longPath;

    /// <summary>修改时间，本地时区</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastWriteTimeText))]
    private DateTime _lastWriteTime;

    /// <summary>类型名，如「文本文档」「文件夹」</summary>
    [ObservableProperty]
    private string _typeName;

    /// <summary>大小文本，文件夹 / 无信息为空串</summary>
    [ObservableProperty]
    private string _sizeText;

    [ObservableProperty]
    private ImageSource? _icon;

    /// <summary>是否可显示缩略图的图片，按扩展名判断</summary>
    public bool IsImage { get; private set; }

    /// <summary>路径是否超长，普通路径 &gt; 259</summary>
    public bool IsTooLong => DisplayPath.Length > LongPathRules.MaxExplorerPathLength;

    [ObservableProperty]
    private bool _isDirectory;

    /// <summary>是否隐藏，列表中半透明</summary>
    [ObservableProperty]
    private bool _isHidden;

    [ObservableProperty]
    private bool _isSystem;

    /// <summary>是否在名称后显示「超长」标签</summary>
    [ObservableProperty]
    private bool _isProblematic;

    /// <summary>是否显示「超长」徽章，路径超 259 或名称含结尾点 / 保留名</summary>
    [ObservableProperty]
    private bool _hasBadge;

    /// <summary>超长 / 无法处理的原因，正常为空串</summary>
    [ObservableProperty]
    private string _issueReason;

    /// <summary>大小，单位字节，文件夹为 0，驱动器为总容量</summary>
    [ObservableProperty]
    private ulong _sizeBytes;

    /// <summary>修改时间显示文本，空值显示空串</summary>
    public string LastWriteTimeText => LastWriteTime == DateTime.MinValue
        ? string.Empty
        : LastWriteTime.ToString("yyyy/M/d HH:mm");

    public DriveItem? Drive { get; }

    public FileEntry? Entry => _entry;

    public bool IsDrive => Drive is not null;

    public string SortName => Name;

    [ObservableProperty]
    private bool _isCut;

    // 递归搜索结果

    /// <summary>递归搜索命中时该条目所在文件夹</summary>
    [ObservableProperty]
    private string _searchFolderPath = string.Empty;

    // 分组

    [ObservableProperty]
    private Core.Grouping.FileGroupKey? _groupKey;

    // 原地重命名状态

    [ObservableProperty]
    private bool _isRenaming;

    /// <summary>编辑框文本，提交前的临时值</summary>
    [ObservableProperty]
    private string _editingName = string.Empty;

    /// <summary>校验提示，显示在编辑框旁，为空不显示</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRenameMessage))]
    [NotifyPropertyChangedFor(nameof(HasRenameError))]
    private string _renameMessage = string.Empty;

    /// <summary>校验提示是否为错误，true 红色 false 灰色</summary>
    [ObservableProperty]
    private bool _isRenameMessageError;

    public bool HasRenameMessage => RenameMessage.Length > 0;

    /// <summary>是否有阻断性错误，回车不允许提交</summary>
    public bool HasRenameError => RenameMessage.Length > 0 && IsRenameMessageError;

    /// <summary>是否允许重命名，驱动器和「此电脑」条目不许可</summary>
    public bool CanRename => !IsDrive;

    /// <summary>悬停提示：完整名称 + 完整路径</summary>
    public string ToolTipText
    {
        get
        {
            string text = $"{Name}\n{DisplayPath}";
            if (IsProblematic)
            {
                text += $"\n{IssueReason}";
            }

            return text;
        }
    }

    public string BadgeToolTip => HasBadge
        ? $"{IssueReason}\n可用「修复路径」处理。"
        : IssueReason;

    /// <summary>重命名成功后就地更新本条目的名称 / 路径 / 图标等信息</summary>
    public void ApplyRename(
        string newName,
        string newLongPath,
        string newDisplayPath,
        uint attributes,
        ulong size,
        DateTime lastWriteTimeUtc)
    {
        Name = newName;
        LongPath = newLongPath;
        DisplayPath = newDisplayPath;
        LastWriteTime = lastWriteTimeUtc == DateTime.MinValue ? DateTime.MinValue : lastWriteTimeUtc.ToLocalTime();
        SizeBytes = size;
        SizeText = IsDirectory ? string.Empty : FormatSize(size);
        TypeName = SystemIconService.GetTypeName(newName, IsDirectory);
        Icon = SystemIconService.GetIcon(newName, IsDirectory);
        IsImage = !IsDirectory && ThumbnailService.IsImageExtension(PathHelper.GetExtension(newName));
        IsHidden = (attributes & 0x0000_0002) != 0;
        IsSystem = (attributes & 0x0000_0004) != 0;

        LongPathAnalysis analysis = LongPathRules.Analyze(newDisplayPath);
        IsProblematic = analysis.IsProblematic;
        IssueReason = analysis.Reason;
        HasBadge = LongPathRules.ShouldShowBadge(newDisplayPath);

        OnPropertyChanged(nameof(SortName));
        OnPropertyChanged(nameof(ToolTipText));
        OnPropertyChanged(nameof(LastWriteTimeText));
    }

    /// <summary>把字节数转成人性化文本；0 显示「0 字节」</summary>
    public static string FormatSize(ulong bytes)
    {
        if (bytes == 0)
        {
            return "0 字节";
        }

        string[] units = { "字节", "KB", "MB", "GB", "TB", "PB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} 字节"
            : $"{value:0.#} {units[unit]}";
    }

    /// <inheritdoc/>
    public override string ToString() => Name;
}
