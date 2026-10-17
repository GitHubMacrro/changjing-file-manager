using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using ChangJing.App.Core.FileSystem;
using ChangJing.App.Core.LongPath;
using ChangJing.App.Core.Operations;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Services;

public enum ClipboardOperation
{
    Copy,

    Cut,
}

/// <summary>内部剪贴板中的一项，同时保存普通路径与扩展路径</summary>
public sealed class ClipboardEntry
{
    public ClipboardEntry(string displayPath, string longPath, bool isDirectory)
    {
        DisplayPath = displayPath;
        LongPath = longPath;
        IsDirectory = isDirectory;
    }

    /// <summary>普通路径</summary>
    public string DisplayPath { get; }

    /// <summary>扩展路径</summary>
    public string LongPath { get; }

    public bool IsDirectory { get; }
}

/// <summary>程序内部剪贴板：保存复制 / 剪切条目，并同步写系统剪贴板 CF_HDROP</summary>
// 系统 CF_HDROP 放不下超 259 字符或特殊名称的路径
public sealed class AppClipboard
{
    /// <summary>全局共享实例，界面与测试共用</summary>
    public static AppClipboard Current { get; } = new();

    // Preferred DropEffect 格式名，资源管理器用它区分复制 / 剪切
    private const string PreferredDropEffectFormat = "Preferred DropEffect";

    private const int DropEffectCopy = 0x0001;

    private const int DropEffectMove = 0x0002;

    // 写系统剪贴板总时间预算，毫秒，避免 UI 线程长时间等待
    private const int WriteBudgetMs = ClipboardService.TotalBudgetMs;

    private const int RetryDelayMs = 15;

    private const int MaxAttempts = 5;

    private readonly List<ClipboardEntry> _entries = new();
    private ClipboardOperation _operation = ClipboardOperation.Copy;
    private uint _systemSequence;
    private bool _wroteSystem;

    // 系统剪贴板按序列号缓存，CanPaste 频繁调用不能每次都开剪贴板
    private bool _externalChecked;
    private uint _externalSequence;
    private bool _externalHasFiles;
    private List<ClipboardEntry> _externalEntries = new();
    private ClipboardOperation _externalOperation = ClipboardOperation.Copy;

    private AppClipboard()
    {
    }

    /// <summary>内部剪贴板内容变化时触发，刷新列表与命令可用性</summary>
    public event EventHandler? Changed;

    public bool HasContent => _entries.Count > 0;

    public ClipboardOperation Operation => _operation;

    /// <summary>当前是否为剪切且有条目，列表据此半透明显示</summary>
    public bool IsCut => _operation == ClipboardOperation.Cut && _entries.Count > 0;

    /// <summary>内部剪贴板中的条目，顺序即操作顺序</summary>
    public IReadOnlyList<ClipboardEntry> Entries => _entries;

    /// <summary>设置内部剪贴板内容，并尝试同步写入系统剪贴板</summary>
    public string SetFiles(ClipboardOperation operation, IReadOnlyList<ClipboardEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        _entries.Clear();
        _entries.AddRange(entries);
        _operation = operation;

        bool cut = operation == ClipboardOperation.Cut;

        // 只有资源管理器能处理的短路径才写系统剪贴板
        var dropPaths = new StringCollection();
        int tooLongCount = 0;
        foreach (ClipboardEntry entry in _entries)
        {
            if (IsExplorerCompatible(entry.DisplayPath))
            {
                dropPaths.Add(entry.DisplayPath);
            }
            else
            {
                tooLongCount++;
            }
        }

        _wroteSystem = dropPaths.Count > 0 && TryWriteFileDrop(dropPaths, cut);
        _systemSequence = SafeSequenceNumber();

        // 刚写入系统剪贴板：外部缓存失效，下次读取时再刷新
        _externalChecked = false;

        Changed?.Invoke(this, EventArgs.Empty);

        var message = new StringBuilder();
        message.Append($"已{(cut ? "剪切" : "复制")} {_entries.Count} 个项目。");
        if (tooLongCount > 0)
        {
            message.Append($"{tooLongCount} 个项目路径过长，只能在畅径内粘贴。");
        }

        LogManager.Info(
            typeof(AppClipboard),
            $"内部剪贴板已{(cut ? "剪切" : "复制")} {_entries.Count} 个项目（写入系统剪贴板：" +
            $"{(_wroteSystem ? dropPaths.Count.ToString() : "无")}；长路径 {tooLongCount} 个）。");

        return message.ToString();
    }

    /// <summary>剪切粘贴成功后清空剪切项，非剪切时不做任何事</summary>
    public bool ClearCut()
    {
        if (_operation != ClipboardOperation.Cut || _entries.Count == 0)
        {
            return false;
        }

        _entries.Clear();
        _operation = ClipboardOperation.Copy;
        _externalChecked = false;

        LogManager.Info(typeof(AppClipboard), "剪切完成，已清空内部剪贴板的剪切项。");
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>是否有可粘贴数据：内部条目或系统剪贴板文件</summary>
    public bool HasPasteData()
    {
        if (_entries.Count > 0)
        {
            return true;
        }

        EnsureExternalUpToDate();
        return _externalHasFiles;
    }

    /// <summary>取本次粘贴的数据：系统更新时用系统，否则用内部</summary>
    public bool TryGetPasteData(out ClipboardOperation operation, out IReadOnlyList<ClipboardEntry> entries)
    {
        operation = _operation;
        entries = _entries;

        uint current = SafeSequenceNumber();
        if (current != _systemSequence)
        {
            EnsureExternalUpToDate();
            if (_externalHasFiles)
            {
                LogManager.Info(
                    typeof(AppClipboard),
                    $"系统剪贴板已更新（序列号 {_systemSequence} → {current}），改用系统剪贴板的 " +
                    $"{_externalEntries.Count} 个文件。");
                operation = _externalOperation;
                entries = _externalEntries;
                return true;
            }
        }

        return _entries.Count > 0;
    }

    private void EnsureExternalUpToDate()
    {
        uint current = SafeSequenceNumber();
        if (_externalChecked && current == _externalSequence)
        {
            return;
        }

        _externalChecked = true;
        _externalSequence = current;

        if (TryReadFileDrop(out List<ClipboardEntry> list, out ClipboardOperation op) && list.Count > 0)
        {
            _externalEntries = list;
            _externalOperation = op;
            _externalHasFiles = true;
        }
        else
        {
            _externalEntries = new List<ClipboardEntry>();
            _externalOperation = ClipboardOperation.Copy;
            _externalHasFiles = false;
        }
    }

    /// <summary>路径是否能放入系统剪贴板 CF_HDROP，长度 ≤ 259 且无特殊名称</summary>
    public static bool IsExplorerCompatible(string? displayPath)
        => !string.IsNullOrEmpty(displayPath) && !LongPathRules.TryDescribeIssue(displayPath, out _);

    // 系统剪贴板

    private static bool TryWriteFileDrop(StringCollection paths, bool cut)
    {
        var stopwatch = Stopwatch.StartNew();

        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                var data = new DataObject();
                data.SetFileDropList(paths);

                if (cut)
                {
                    // 资源管理器读取 Preferred DropEffect = Move 才按剪切处理
                    var effect = new MemoryStream(BitConverter.GetBytes(DropEffectMove));
                    data.SetData(PreferredDropEffectFormat, effect);
                }

                Clipboard.SetDataObject(data, copy: true);
                return true;
            }
            catch (Exception ex)
            {
                LogManager.Warning(
                    typeof(AppClipboard),
                    $"写系统剪贴板（文件列表）失败（第 {attempt + 1} 次，已用 {stopwatch.ElapsedMilliseconds} ms）：{ex.Message}");

                if (attempt >= MaxAttempts - 1 || stopwatch.ElapsedMilliseconds + RetryDelayMs > WriteBudgetMs)
                {
                    break;
                }

                Thread.Sleep(RetryDelayMs);
            }
        }

        return false;
    }

    private static bool TryReadFileDrop(out List<ClipboardEntry> entries, out ClipboardOperation operation)
    {
        entries = new List<ClipboardEntry>();
        operation = ClipboardOperation.Copy;

        try
        {
            IDataObject? data = Clipboard.GetDataObject();
            if (data is null || !data.GetDataPresent(DataFormats.FileDrop))
            {
                return false;
            }

            if (data.GetData(DataFormats.FileDrop) is not string[] files)
            {
                return false;
            }

            operation = ReadDropEffect(data) == DropEffectMove
                ? ClipboardOperation.Cut
                : ClipboardOperation.Copy;

            foreach (string file in files)
            {
                if (string.IsNullOrEmpty(file))
                {
                    continue;
                }

                string longPath = PathHelper.TryToLong(file, out string extended, out _) ? extended : file;
                entries.Add(new ClipboardEntry(PathHelper.ToDisplay(file), longPath, IsDirectorySafe(longPath)));
            }

            return entries.Count > 0;
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(AppClipboard), $"读取系统剪贴板（文件列表）失败：{ex.Message}");
            return false;
        }
    }

    private static int ReadDropEffect(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent(PreferredDropEffectFormat))
            {
                return DropEffectCopy;
            }

            object? value = data.GetData(PreferredDropEffectFormat);
            byte[]? buffer = value switch
            {
                MemoryStream stream => ReadStream(stream),
                byte[] bytes => bytes,
                _ => null,
            };

            return buffer is { Length: >= 4 }
                ? BitConverter.ToInt32(buffer, 0)
                : DropEffectCopy;
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(AppClipboard), $"读取 Preferred DropEffect 失败：{ex.Message}");
            return DropEffectCopy;
        }
    }

    private static byte[]? ReadStream(MemoryStream stream)
    {
        if (stream.Length < 4)
        {
            return null;
        }

        var buffer = new byte[4];
        stream.Position = 0;
        int read = stream.Read(buffer, 0, 4);
        return read == 4 ? buffer : null;
    }

    private static bool IsDirectorySafe(string longPath)
    {
        try
        {
            return FileOperations.TryGetEntryInfo(longPath, out uint attributes, out _, out _)
                   && (attributes & NativeMethods.FileAttributeDirectory) != 0;
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(AppClipboard), $"判断条目是否为文件夹失败（{longPath}）：{ex.Message}");
            return false;
        }
    }

    private static uint SafeSequenceNumber()
    {
        try
        {
            return NativeMethods.GetClipboardSequenceNumber();
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(AppClipboard), $"读取剪贴板序列号失败：{ex.Message}");
            return 0;
        }
    }
}
