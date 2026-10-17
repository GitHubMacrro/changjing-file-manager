using System.Collections.Specialized;
using System.Windows;
using ChangJing.App.Core.LongPath;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Services;

/// <summary>拖放数据对象的构造与读取：拖出时生成 CF_HDROP，拖入时读取 CF_HDROP</summary>
// 拖出只允许复制：Preferred DropEffect 固定为 Copy，外部程序请求移动也不会删源
public static class DragDropService
{
    // Preferred DropEffect 格式名
    private const string PreferredDropEffectFormat = "Preferred DropEffect";

    private const int DropEffectCopy = 0x0001;

    public sealed class DragOutData
    {
        internal DragOutData(IDataObject? data, int included, int skipped)
        {
            Data = data;
            IncludedCount = included;
            SkippedCount = skipped;
        }

        /// <summary>数据对象；无可拖出项时为 null</summary>
        public IDataObject? Data { get; }

        public int IncludedCount { get; }

        /// <summary>因路径过长 / 名称特殊而无法拖出的项数</summary>
        public int SkippedCount { get; }

        /// <summary>是否需要向用户提示「路径过长无法拖出其它程序」</summary>
        public bool HasSkipped => SkippedCount > 0;
    }

    /// <summary>为拖出构造 CF_HDROP 数据对象，只申报复制效果</summary>
    // 长路径项换成短根联接路径，转换失败的项被跳过
    public static DragOutData BuildDragOutData(IReadOnlyList<string> displayPaths)
    {
        var dropPaths = new StringCollection();
        int skipped = 0;

        if (displayPaths is not null)
        {
            foreach (string displayPath in displayPaths)
            {
                if (string.IsNullOrWhiteSpace(displayPath))
                {
                    continue;
                }

                string path = PathHelper.ToDisplay(displayPath);
                if (AppClipboard.IsExplorerCompatible(path))
                {
                    dropPaths.Add(path);
                    continue;
                }

                // 长路径 / 名称特殊：尝试用短根联接换成短路径
                if (OpenShortRoot.TryPrepareFile(path, out string? shortPath) && !string.IsNullOrEmpty(shortPath))
                {
                    dropPaths.Add(shortPath!);
                    LogManager.Info(typeof(DragDropService), $"拖出长路径项改用短根联接：{path} → {shortPath}");
                }
                else
                {
                    skipped++;
                    LogManager.Warning(typeof(DragDropService), $"拖出项路径过长且无法生成短路径，已跳过：{path}");
                }
            }
        }

        if (dropPaths.Count == 0)
        {
            return new DragOutData(null, 0, skipped);
        }

        try
        {
            var data = new DataObject();
            data.SetFileDropList(dropPaths);

            // 只申报复制：外部程序即便请求移动，系统也不会删源
            var effect = new System.IO.MemoryStream(BitConverter.GetBytes(DropEffectCopy));
            data.SetData(PreferredDropEffectFormat, effect);

            return new DragOutData(data, dropPaths.Count, skipped);
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(DragDropService), $"构造拖出数据对象失败：{ex.Message}");
            return new DragOutData(null, 0, skipped + dropPaths.Count);
        }
    }

    /// <summary>从拖入 / 剪贴板数据对象里读取 CF_HDROP 文件列表，普通路径</summary>
    public static IReadOnlyList<string> ReadFileDrop(IDataObject? data)
    {
        if (data is null)
        {
            return Array.Empty<string>();
        }

        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop))
            {
                return Array.Empty<string>();
            }

            if (data.GetData(DataFormats.FileDrop) is not string[] files)
            {
                return Array.Empty<string>();
            }

            var result = new List<string>(files.Length);
            foreach (string file in files)
            {
                if (!string.IsNullOrWhiteSpace(file))
                {
                    result.Add(PathHelper.ToDisplay(file));
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(DragDropService), $"读取拖入文件列表失败：{ex.Message}");
            return Array.Empty<string>();
        }
    }
}
