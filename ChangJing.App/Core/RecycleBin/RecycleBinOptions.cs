namespace ChangJing.App.Core.RecycleBin;

/// <summary>程序自有回收站的存储选项</summary>
public sealed class RecycleBinOptions
{
    /// <summary>回收站目录名，各卷根下隐藏加系统属性</summary>
    public const string DefaultDirectoryName = "$ChangJing.Recycle";

    /// <summary>默认容量上限：卷容量的 10%</summary>
    public const double DefaultCapacityPercent = 10.0;

    public string DirectoryName { get; init; } = DefaultDirectoryName;

    /// <summary>可选的根路径覆盖，仅验证用：卷根返回基础目录，null 时卷根加 DirectoryName</summary>
    public Func<string, string?>? RootOverride { get; init; }

    /// <summary>可选的卷根列表覆盖，仅验证用；null 时枚举本机卷</summary>
    public Func<IReadOnlyList<string>>? VolumeRootsOverride { get; init; }

    /// <summary>容量上限占卷容量的百分比，默认 10，0 表示不限制</summary>
    public double CapacityPercent { get; set; } = DefaultCapacityPercent;

    /// <summary>容量上限的固定字节数覆盖，大于 0 时优先，0 用百分比</summary>
    public long CapacityFixedBytes { get; set; }
}
