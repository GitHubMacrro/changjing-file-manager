using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ChangJing.App.Core.Shell;
using ChangJing.App.Services;
using ChangJing.App.Services.Preview;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Controls;

public sealed class PreviewPaneOpenEventArgs : EventArgs
{
    public required PreviewTarget Target { get; init; }
}

public sealed class PreviewPaneCopyEventArgs : EventArgs
{
    public required PreviewTarget Target { get; init; }
}

public sealed class PreviewPaneRepairEventArgs : EventArgs
{
    public required PreviewTarget Target { get; init; }
}

/// <summary>预览窗格：按选中项显示图片 / 文本 / PDF 等预览</summary>
// 全部加载在后台线程，界面线程只负责显示
public partial class PreviewPane : UserControl
{
    private const int DebounceMilliseconds = 150;
    private const double ReDecodeThreshold = 0.2;

    private readonly DispatcherTimer _debounceTimer;

    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _pdfRenderCts;

    private PreviewTarget? _currentTarget;
    private PreviewTarget? _pendingTarget;
    private CancellationToken _pendingToken;

    private PdfPreviewHandle? _pdfHandle;
    private int _pdfPageIndex;
    private PreviewKind _currentKind = PreviewKind.None;
    private int _lastDecodeWidth;

    public PreviewPane()
    {
        InitializeComponent();

        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMilliseconds) };
        _debounceTimer.Tick += OnDebounceTick;

        SizeChanged += OnPaneSizeChanged;
        Unloaded += OnUnloaded;

        PreviewPaneSystem.LoadCompleted += OnSystemPreviewLoaded;

        ShowEmpty();
    }

    public event EventHandler<PreviewPaneOpenEventArgs>? OpenRequested;

    public event EventHandler<PreviewPaneCopyEventArgs>? CopyToDesktopRequested;

    public event EventHandler<PreviewPaneRepairEventArgs>? RepairRequested;

    public bool IsSystemPreviewLoaded => PreviewPaneSystem.IsHandlerLoaded;

    public Guid SystemPreviewHandlerClsid => PreviewPaneSystem.LastHandlerClsid;

    public bool IsSystemHostCreated => PreviewPaneSystem.IsHostCreated;

    public SystemPreviewStatus SystemPreviewLastStatus => PreviewPaneSystem.LastStatus;

    // 公共 API

    public void ShowEmpty()
    {
        try
        {
            CancelPending();
            _currentTarget = null;
            _currentKind = PreviewKind.None;

            ShowHost(null);
            ShowMessage("选择文件以预览");
            UpdateButtonState();
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(PreviewPane), ex);
        }
    }

    public void ShowMultiple(int count)
    {
        try
        {
            CancelPending();
            _currentTarget = null;
            _currentKind = PreviewKind.None;

            OtherIcon.Source = SystemIconService.GetIconForSize("changjing", isDirectory: false, 48);
            OtherName.Text = $"已选择 {count} 个项目";
            OtherMeta.Text = string.Empty;
            OtherModified.Text = string.Empty;
            OtherPath.Text = string.Empty;
            OtherHint.Text = string.Empty;
            OtherHint.Visibility = Visibility.Collapsed;

            ShowHost(PreviewPaneOther);
            HideMessage();
            UpdateButtonState();
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(PreviewPane), ex);
        }
    }

    /// <summary>显示单个目标的预览；null 等价于 ShowEmpty</summary>
    public void ShowTarget(PreviewTarget? target)
    {
        if (target is null)
        {
            ShowEmpty();
            return;
        }

        try
        {
            CancelPending();
            _currentTarget = target;
            _currentKind = PreviewTypeRules.Classify(target.Name, target.IsDirectory);
            UpdateButtonState();

            if (_currentKind == PreviewKind.Other || _currentKind == PreviewKind.None)
            {
                // 内置预览不支持的类型，查注册表看有没有系统预览处理器
                if (!target.IsDirectory
                    && !target.IsDrive
                    && PreviewHandlerRegistry.Find(Core.LongPath.PathHelper.GetExtension(target.Name)) is not null)
                {
                    ShowHost(PreviewPaneSystem);
                    ShowMessage("正在预览…");

                    // 宿主从 Collapsed 变 Visible 后需一次布局才创建子窗口，故推迟到 Loaded 优先级
                    Dispatcher.BeginInvoke(
                        DispatcherPriority.Loaded,
                        new Action(() =>
                        {
                            if (ReferenceEquals(target, _currentTarget))
                            {
                                _ = LoadSystemPreviewAsync(target);
                            }
                        }));
                    return;
                }

                ApplyOther(target);
                ShowHost(PreviewPaneOther);
                HideMessage();
                return;
            }

            ShowHost(null);
            ShowMessage("正在预览…");
            ScheduleLoad();
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(PreviewPane), ex);
            ShowHost(null);
            ShowMessage("无法预览此文件");
        }
    }

    // 加载调度

    // 3 秒超时 / 失败回落到「其他类型」信息页
    private async Task LoadSystemPreviewAsync(PreviewTarget target)
    {
        SystemPreviewResult result;
        try
        {
            result = await PreviewPaneSystem.LoadAsync(target, IsDarkTheme());
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(PreviewPane), ex);
            result = SystemPreviewResult.CreateFailed(Guid.Empty, "加载系统预览失败。");
        }

        // 迟到的结果：目标已被换掉
        if (!ReferenceEquals(target, _currentTarget))
        {
            PreviewPaneSystem.Unload();
            return;
        }

        if (result.IsLoaded)
        {
            ShowHost(PreviewPaneSystem);
            HideMessage();
            return;
        }

        PreviewPaneSystem.Unload();

        if (result.Status is SystemPreviewStatus.Timeout or SystemPreviewStatus.Failed)
        {
            ShowHost(null);
            ShowMessage("无法预览此文件");
            return;
        }

        ApplyOther(target);
        ShowHost(PreviewPaneOther);
        HideMessage();
    }

    private void OnSystemPreviewLoaded(object? sender, SystemPreviewLoadedEventArgs e) => _ = e;

    private static bool IsDarkTheme()
    {
        try
        {
            if (Application.Current?.TryFindResource("CjWindowBackgroundBrush") is SolidColorBrush brush)
            {
                System.Windows.Media.Color color = brush.Color;
                double luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
                return luminance < 0.5;
            }
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(PreviewPane), $"判断深色主题失败：{ex.Message}");
        }

        return false;
    }

    private void ScheduleLoad()
    {
        PreviewTarget? target = _currentTarget;
        if (target is null)
        {
            return;
        }

        CancelLoadToken();

        var cts = new CancellationTokenSource();
        _loadCts = cts;
        _pendingTarget = target;
        _pendingToken = cts.Token;

        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounceTimer.Stop();

        PreviewTarget? target = _pendingTarget;
        if (target is null)
        {
            return;
        }

        _pendingTarget = null;
        _ = LoadAsync(target, _pendingToken);
    }

    private async Task LoadAsync(PreviewTarget target, CancellationToken ct)
    {
        try
        {
            if (ct.IsCancellationRequested || !ReferenceEquals(target, _currentTarget))
            {
                return;
            }

            int width = CurrentDecodeWidth();
            _lastDecodeWidth = width;

            switch (_currentKind)
            {
                case PreviewKind.Image:
                    await LoadImageAsync(target, width, ct).ConfigureAwait(true);
                    break;

                case PreviewKind.Text:
                    await LoadTextAsync(target, ct).ConfigureAwait(true);
                    break;

                case PreviewKind.Pdf:
                    await LoadPdfAsync(target, ct).ConfigureAwait(true);
                    break;

                default:
                    ApplyOther(target);
                    ShowHost(PreviewPaneOther);
                    HideMessage();
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(PreviewPane), ex);
            if (IsCurrent(target))
            {
                ShowHost(null);
                ShowMessage("无法预览此文件");
            }
        }
    }

    private async Task LoadImageAsync(PreviewTarget target, int width, CancellationToken ct)
    {
        System.Windows.Media.ImageSource? image = await ImagePreviewLoader.LoadAsync(target.LongPath, width, ct);
        if (ct.IsCancellationRequested || !IsCurrent(target))
        {
            return;
        }

        if (image is null)
        {
            ShowHost(null);
            ShowMessage(target.HasBadge ? "路径过长，无法预览" : "无法预览此文件");
            return;
        }

        PreviewImage.Source = image;
        ShowHost(PreviewPaneImage);
        HideMessage();
    }

    private async Task LoadTextAsync(PreviewTarget target, CancellationToken ct)
    {
        TextPreview preview = await TextPreviewLoader.LoadAsync(target.LongPath, ct);
        if (ct.IsCancellationRequested || !IsCurrent(target))
        {
            return;
        }

        if (ReferenceEquals(preview, TextPreview.Empty))
        {
            ShowHost(null);
            ShowMessage(target.HasBadge ? "路径过长，无法预览" : "无法预览此文件");
            return;
        }

        ApplyText(preview);
        ShowHost(PreviewPaneText);
        HideMessage();
    }

    private async Task LoadPdfAsync(PreviewTarget target, CancellationToken ct)
    {
        PdfPreviewHandle? handle = await PdfPreviewLoader.OpenAsync(target.DisplayPath, target.LongPath, ct);
        if (ct.IsCancellationRequested || !IsCurrent(target))
        {
            handle?.Dispose();
            return;
        }

        if (handle is null)
        {
            ShowHost(null);
            ShowMessage(target.HasBadge ? "路径过长，无法预览" : "无法预览此文件");
            return;
        }

        _pdfHandle = handle;
        _pdfPageIndex = 0;
        ShowHost(PreviewPanePdf);
        HideMessage();
        await RenderPdfPageAsync().ConfigureAwait(true);
    }

    private async Task RenderPdfPageAsync()
    {
        PdfPreviewHandle? handle = _pdfHandle;
        if (handle is null || !handle.IsValid)
        {
            return;
        }

        CancellationTokenSource? previous = _pdfRenderCts;
        var cts = new CancellationTokenSource();
        _pdfRenderCts = cts;
        previous?.Cancel();
        previous?.Dispose();

        CancellationToken ct = cts.Token;
        int width = CurrentDecodeWidth();
        _lastDecodeWidth = width;

        UpdatePdfNav(handle.Info.PageCount);

        try
        {
            System.Windows.Media.ImageSource? image = await handle.RenderPageAsync(_pdfPageIndex, width, ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(handle, _pdfHandle))
            {
                return;
            }

            if (image is null)
            {
                PdfImage.Source = null;
                ShowMessage("无法预览此文件");
                return;
            }

            PdfImage.Source = image;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(PreviewPane), ex);
            if (ReferenceEquals(handle, _pdfHandle))
            {
                ShowMessage("无法预览此文件");
            }
        }
    }

    // 显示辅助

    private void ApplyText(TextPreview preview)
    {
        TextBody.Text = preview.Text;

        string header = preview.EncodingName;
        if (preview.Truncated)
        {
            header = header.Length > 0 ? header + " · 仅显示前 1 MB" : "仅显示前 1 MB";
        }

        TextHeader.Text = header;
        TextHeader.Visibility = header.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyOther(PreviewTarget target)
    {
        OtherIcon.Source = target.Icon48;
        OtherName.Text = target.Name;

        var meta = new List<string>(2);
        if (target.TypeName.Length > 0)
        {
            meta.Add(target.TypeName);
        }

        if (target.SizeText.Length > 0)
        {
            meta.Add(target.SizeText);
        }

        OtherMeta.Text = string.Join(" · ", meta);

        OtherModified.Text = target.LastWriteTime == DateTime.MinValue
            ? string.Empty
            : "修改时间 " + target.LastWriteTime.ToString("yyyy/M/d HH:mm");

        OtherPath.Text = target.DisplayPath;

        if (target.HasBadge && target.IssueReason.Length > 0)
        {
            OtherHint.Text = target.IssueReason + "\n可用「修复路径」处理。";
            OtherHint.Visibility = Visibility.Visible;
            PreviewPaneRepairButton.Visibility = target.IsDrive ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            OtherHint.Text = string.Empty;
            OtherHint.Visibility = Visibility.Collapsed;
            PreviewPaneRepairButton.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowHost(UIElement? host)
    {
        PreviewPaneSystem.Visibility = ReferenceEquals(host, PreviewPaneSystem) ? Visibility.Visible : Visibility.Collapsed;
        PreviewPaneImage.Visibility = ReferenceEquals(host, PreviewPaneImage) ? Visibility.Visible : Visibility.Collapsed;
        PreviewPaneText.Visibility = ReferenceEquals(host, PreviewPaneText) ? Visibility.Visible : Visibility.Collapsed;
        PreviewPanePdf.Visibility = ReferenceEquals(host, PreviewPanePdf) ? Visibility.Visible : Visibility.Collapsed;
        PreviewPaneOther.Visibility = ReferenceEquals(host, PreviewPaneOther) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowMessage(string text)
    {
        MessageText.Text = text;
        PreviewPaneMessage.Visibility = Visibility.Visible;
    }

    private void HideMessage()
    {
        PreviewPaneMessage.Visibility = Visibility.Collapsed;
    }

    private void UpdateButtonState()
    {
        bool enabled = _currentTarget is not null;
        PreviewPaneOpenButton.IsEnabled = enabled;
        PreviewPaneCopyButton.IsEnabled = enabled;
    }

    private void UpdatePdfNav(int pageCount)
    {
        PreviewPanePdfPageLabel.Text = pageCount > 0
            ? $"第 {_pdfPageIndex + 1} / {pageCount} 页"
            : "第 0 / 0 页";

        PreviewPanePdfPrevButton.IsEnabled = _pdfPageIndex > 0;
        PreviewPanePdfNextButton.IsEnabled = pageCount > 0 && _pdfPageIndex < pageCount - 1;
    }

    private int CurrentDecodeWidth()
    {
        double width = PreviewArea.ActualWidth;
        if (width <= 0)
        {
            width = ActualWidth;
        }

        if (width <= 0)
        {
            width = 800;
        }

        return (int)Math.Max(64, width);
    }

    private bool IsCurrent(PreviewTarget target) => ReferenceEquals(target, _currentTarget);

    // 取消 / 清理

    private void CancelPending()
    {
        _debounceTimer.Stop();
        _pendingTarget = null;
        CancelLoadToken();
        CancelPdfRenderToken();
        DisposePdfHandle();

        try
        {
            PreviewPaneSystem.Unload();
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(PreviewPane), $"卸载系统预览失败：{ex.Message}");
        }

        PreviewPaneSystem.Visibility = Visibility.Collapsed;

        PreviewImage.Source = null;
        PdfImage.Source = null;
    }

    /// <summary>暂时隐藏 / 恢复系统预览宿主，避免 HwndHost 遮住弹出层</summary>
    public void SetSystemPreviewHostVisible(bool visible)
    {
        try
        {
            PreviewPaneSystem.SetHostVisible(visible);
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(PreviewPane), $"切换系统预览宿主可见性失败：{ex.Message}");
        }
    }

    private void CancelLoadToken()
    {
        CancellationTokenSource? cts = _loadCts;
        _loadCts = null;
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(PreviewPane), $"取消加载失败：{ex.Message}");
        }

        cts.Dispose();
    }

    private void CancelPdfRenderToken()
    {
        CancellationTokenSource? cts = _pdfRenderCts;
        _pdfRenderCts = null;
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(PreviewPane), $"取消 PDF 渲染失败：{ex.Message}");
        }

        cts.Dispose();
    }

    private void DisposePdfHandle()
    {
        PdfPreviewHandle? handle = _pdfHandle;
        _pdfHandle = null;
        _pdfPageIndex = 0;

        try
        {
            handle?.Dispose();
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(PreviewPane), $"释放 PDF 预览失败：{ex.Message}");
        }
    }

    // 事件处理

    private void OnPaneSizeChanged(object sender, SizeChangedEventArgs e)
    {
        try
        {
            if (_currentTarget is null || _lastDecodeWidth <= 0)
            {
                return;
            }

            if (_currentKind is not (PreviewKind.Image or PreviewKind.Pdf))
            {
                return;
            }

            int width = CurrentDecodeWidth();
            double delta = Math.Abs(width - _lastDecodeWidth) / (double)_lastDecodeWidth;
            if (delta < ReDecodeThreshold)
            {
                return;
            }

            if (_currentKind == PreviewKind.Pdf)
            {
                _ = RenderPdfPageAsync();
            }
            else
            {
                ScheduleLoad();
            }
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(PreviewPane), $"缩放后重新预览失败：{ex.Message}");
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        try
        {
            CancelPending();
            _currentTarget = null;
            _currentKind = PreviewKind.None;
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(PreviewPane), $"卸载时清理失败：{ex.Message}");
        }
    }

    private void OnPdfPrevClick(object sender, RoutedEventArgs e)
    {
        if (_pdfHandle is null || _pdfPageIndex <= 0)
        {
            return;
        }

        _pdfPageIndex--;
        _ = RenderPdfPageAsync();
    }

    private void OnPdfNextClick(object sender, RoutedEventArgs e)
    {
        if (_pdfHandle is null || _pdfPageIndex >= _pdfHandle.Info.PageCount - 1)
        {
            return;
        }

        _pdfPageIndex++;
        _ = RenderPdfPageAsync();
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        PreviewTarget? target = _currentTarget;
        if (target is null)
        {
            return;
        }

        OpenRequested?.Invoke(this, new PreviewPaneOpenEventArgs { Target = target });
    }

    private void OnCopyToDesktopClick(object sender, RoutedEventArgs e)
    {
        PreviewTarget? target = _currentTarget;
        if (target is null)
        {
            return;
        }

        CopyToDesktopRequested?.Invoke(this, new PreviewPaneCopyEventArgs { Target = target });
    }

    private void OnRepairClick(object sender, RoutedEventArgs e)
    {
        PreviewTarget? target = _currentTarget;
        if (target is null)
        {
            return;
        }

        RepairRequested?.Invoke(this, new PreviewPaneRepairEventArgs { Target = target });
    }
}
