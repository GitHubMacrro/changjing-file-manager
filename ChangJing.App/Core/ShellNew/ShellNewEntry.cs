using System;
using System.Collections.Generic;

namespace ChangJing.App.Core.ShellNew;

/// <summary>ShellNew 注册表项决定「新建」时如何生成文件</summary>
public enum ShellNewKind
{
    /// <summary>建空文件，NullFile</summary>
    NullFile,

    /// <summary>从模板文件复制，FileName</summary>
    FileName,

    /// <summary>把内联字节写入文件，Data</summary>
    Data,
}

/// <summary>一个可用的「新建」类型，来自注册表 ShellNew</summary>
public sealed class ShellNewEntry
{
    /// <summary>扩展名，含点</summary>
    public string Extension { get; init; } = "";

    /// <summary>ProgID，未知时为空</summary>
    public string? ProgId { get; init; }

    /// <summary>资源解析后的友好名称，不含扩展名，如「文本文档」</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>生成方式，取 NullFile / FileName / Data</summary>
    public ShellNewKind Kind { get; init; } = ShellNewKind.NullFile;

    /// <summary>FileName 时解析出的模板文件绝对路径</summary>
    public string? TemplatePath { get; init; }

    /// <summary>Data 时的内联内容</summary>
    public byte[]? Data { get; init; }

    /// <summary>图标文件路径，取自 ProgID 的 DefaultIcon，无则为 null</summary>
    public string? IconPath { get; init; }

    /// <summary>图标索引，DefaultIcon 中逗号后的数字，无则为 0</summary>
    public int IconIndex { get; init; }

    /// <summary>菜单标签默认文件名，如「新建 文本文档.txt」</summary>
    public string DefaultFileName => "新建 " + DisplayName + Extension;

    /// <summary>返回默认文件名，便于绑定 / 调试</summary>
    public override string ToString() => DefaultFileName;
}

/// <summary>一次 ShellNew 枚举的结果</summary>
public sealed class ShellNewEnumerationResult
{
    /// <summary>可用的新建项，按显示名排序</summary>
    public IReadOnlyList<ShellNewEntry> Entries { get; init; } = Array.Empty<ShellNewEntry>();

    /// <summary>因是 Command 类型而被跳过的扩展名数量</summary>
    public int SkippedCommandTypes { get; init; }

    /// <summary>因声明自定义 Handler 而跳过的数量，创建逻辑在处理器里无法复现</summary>
    public int SkippedHandlerTypes { get; init; }
}
