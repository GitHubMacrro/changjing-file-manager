using System.Threading;
using System.Windows.Media;
using ChangJing.App.Core.LongPath;
using ChangJing.App.Core.Operations;
using ChangJing.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CustomWin.Utils.Logs;

namespace ChangJing.App.ViewModels;

/// <summary>属性对话框视图模型：只读展示，文件夹大小后台统计</summary>
public sealed partial class FilePropertiesViewModel : ObservableObject
{
    private readonly CancellationTokenSource _sizeCts = new();

    private FilePropertiesViewModel(FilePropertiesSnapshot snapshot)
    {
        Snapshot = snapshot;

        Name = snapshot.Name;
        DisplayPath = snapshot.DisplayPath;
        PathLengthText = $"{snapshot.PathLength} 个字符" + (snapshot.IsTooLong ? "（超过 259，属于长路径）" : string.Empty);
        TypeName = snapshot.TypeName;
        AttributesText = snapshot.AttributesText;
        CreationTimeText = FormatTime(snapshot.CreationTime);
        LastWriteTimeText = FormatTime(snapshot.LastWriteTime);
        LastAccessTimeText = FormatTime(snapshot.LastAccessTime);
        Icon = SystemIconService.GetIconForSize(snapshot.Name, snapshot.IsDirectory, 48);

        if (snapshot.IsDirectory)
        {
            SizeText = "正在计算…";
            IsSizeCalculating = true;
        }
        else
        {
            SizeText = $"{FileItemViewModel.FormatSize(snapshot.SizeBytes)}（{snapshot.SizeBytes:N0} 字节）";
        }
    }

    /// <summary>属性快照，未加工数据</summary>
    public FilePropertiesSnapshot Snapshot { get; }

    public string Name { get; }

    /// <summary>完整普通路径</summary>
    public string DisplayPath { get; }

    public string PathLengthText { get; }

    public string TypeName { get; }

    public string AttributesText { get; }

    public string CreationTimeText { get; }

    public string LastWriteTimeText { get; }

    public string LastAccessTimeText { get; }

    public ImageSource? Icon { get; }

    [ObservableProperty]
    private string _sizeText;

    [ObservableProperty]
    private bool _isSizeCalculating;

    /// <summary>创建属性视图模型，读不到属性返回 null</summary>
    public static FilePropertiesViewModel? Create(string displayPath)
    {
        FilePropertiesSnapshot? snapshot = FilePropertiesProvider.TryRead(displayPath);
        return snapshot is null ? null : new FilePropertiesViewModel(snapshot);
    }

    /// <summary>后台统计文件夹大小，可取消</summary>
    public async Task MeasureFolderSizeAsync()
    {
        if (!Snapshot.IsDirectory)
        {
            return;
        }

        string longPath = Snapshot.LongPath;
        CancellationToken token = _sizeCts.Token;

        try
        {
            (int fileCount, long totalBytes)? measured = await Task.Run(
                () => FilePropertiesProvider.TryMeasureFolder(longPath, token, out _),
                token).ConfigureAwait(true);

            if (token.IsCancellationRequested)
            {
                return;
            }

            if (measured is null)
            {
                SizeText = "无法计算（可能没有访问权限）。";
                return;
            }

            (int files, long bytes) = measured.Value;
            SizeText = bytes <= 0
                ? "空文件夹"
                : $"{FileItemViewModel.FormatSize((ulong)bytes)}（{bytes:N0} 字节，共 {files} 个文件）";
        }
        catch (OperationCanceledException)
        {
            // 用户取消，保持界面不变
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(FilePropertiesViewModel), $"统计文件夹大小失败：{ex.Message}");
            SizeText = "无法计算（详细信息已写入日志）。";
        }
        finally
        {
            IsSizeCalculating = false;
        }
    }

    public void CancelSizeCalculation()
    {
        try
        {
            if (!_sizeCts.IsCancellationRequested)
            {
                _sizeCts.Cancel();
            }
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(FilePropertiesViewModel), $"取消大小统计失败：{ex.Message}");
        }

        IsSizeCalculating = false;
    }

    private static string FormatTime(DateTime value)
        => value == DateTime.MinValue ? "（无记录）" : value.ToString("yyyy年M月d日 HH:mm:ss");
}
