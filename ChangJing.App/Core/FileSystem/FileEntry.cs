using ChangJing.App.Core.LongPath;

namespace ChangJing.App.Core.FileSystem;

/// <summary>枚举出来的一个文件 / 文件夹条目，不可变</summary>
public sealed class FileEntry
{
    public FileEntry(string name, string fullPath, uint attributes, ulong size, DateTime lastWriteTimeUtc)
    {
        Name = name;
        FullPath = fullPath;
        Attributes = attributes;
        Size = size;
        LastWriteTimeUtc = lastWriteTimeUtc;
    }

    /// <summary>文件 / 文件夹名，不含路径</summary>
    public string Name { get; }

    /// <summary>完整扩展路径，形如 \\?\C:\...</summary>
    public string FullPath { get; }

    /// <summary>界面显示用的普通路径，永不含 \\?\</summary>
    public string DisplayPath => PathHelper.ToDisplay(FullPath);

    /// <summary>Win32 文件属性位</summary>
    public uint Attributes { get; }

    /// <summary>文件字节大小；文件夹恒为 0</summary>
    public ulong Size { get; }

    /// <summary>修改时间，UTC</summary>
    public DateTime LastWriteTimeUtc { get; }

    /// <summary>修改时间，本地时区</summary>
    public DateTime LastWriteTime => LastWriteTimeUtc == DateTime.MinValue
        ? DateTime.MinValue
        : LastWriteTimeUtc.ToLocalTime();

    public bool IsDirectory => HasAttribute(NativeMethods.FileAttributeDirectory);

    public bool IsHidden => HasAttribute(NativeMethods.FileAttributeHidden);

    public bool IsSystem => HasAttribute(NativeMethods.FileAttributeSystem);

    public bool IsReadOnly => HasAttribute(NativeMethods.FileAttributeReadOnly);

    /// <summary>是否重解析点，如符号链接、目录联接、OneDrive 占位</summary>
    public bool IsReparsePoint => HasAttribute(NativeMethods.FileAttributeReparsePoint);

    public bool IsEncrypted => HasAttribute(NativeMethods.FileAttributeEncrypted);

    public bool IsCompressed => HasAttribute(NativeMethods.FileAttributeCompressed);

    public bool IsHiddenOrSystem => IsHidden || IsSystem;

    /// <summary>是否为枚举器内部的 . / .. 伪条目</summary>
    public bool IsDotEntry => Name == "." || Name == "..";

    public bool HasAttribute(uint attribute) => (Attributes & attribute) != 0;

    /// <inheritdoc/>
    public override string ToString() => $"{Name} ({(IsDirectory ? "文件夹" : $"{Size} 字节")})";
}
