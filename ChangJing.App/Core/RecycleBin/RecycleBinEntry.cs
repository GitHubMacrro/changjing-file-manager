using ChangJing.App.Core.LongPath;

namespace ChangJing.App.Core.RecycleBin;

/// <summary>程序自有回收站的一个删除项：原路径、删除时间、大小与位置</summary>
public sealed class RecycleBinEntry
{
    /// <summary>唯一 ID GUID，同时是条目在回收站内的目录 / 文件名</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>删除前的完整普通路径，还原目标</summary>
    public string OriginalPath { get; init; } = string.Empty;

    /// <summary>删除时间，UTC</summary>
    public DateTime DeletedUtc { get; init; }

    /// <summary>大小按字节：文件为自身大小，文件夹为内部合计</summary>
    public long SizeBytes { get; init; }

    public bool IsDirectory { get; init; }

    /// <summary>文件夹内的文件数，文件恒为 1</summary>
    public int ItemCount { get; init; }

    /// <summary>所在卷的根，普通路径</summary>
    public string VolumeRoot { get; init; } = string.Empty;

    /// <summary>条目本体的完整普通路径，位于回收站内</summary>
    public string ItemPath { get; init; } = string.Empty;

    /// <summary>元数据 JSON 的完整普通路径</summary>
    public string MetadataPath { get; init; } = string.Empty;

    /// <summary>删除前的名称</summary>
    public string Name => PathHelper.GetName(OriginalPath);

    /// <summary>删除时间，本地时区界面显示用</summary>
    public DateTime DeletedLocal
    {
        get
        {
            if (DeletedUtc == DateTime.MinValue)
            {
                return DateTime.MinValue;
            }

            DateTime utc = DeletedUtc.Kind == DateTimeKind.Utc
                ? DeletedUtc
                : DateTime.SpecifyKind(DeletedUtc, DateTimeKind.Utc);
            return utc.ToLocalTime();
        }
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Name}（{OriginalPath}，{DeletedUtc:u}）";
}
