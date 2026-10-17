using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ChangJing.App.Controls;
using ChangJing.App.Services;
using ChangJing.App.ViewModels;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Views;

/// <summary>MainWindow 的标题栏部分：应用图标 / 管理员盾牌 / 标题悬停 ToolTip</summary>
public partial class MainWindow
{
    // 标题内容不参与命中测试，WPF ToolTip 拿不到悬停事件，只能轮询鼠标位置手动弹
    private DispatcherTimer? _titleHoverTimer;

    private DateTime? _titleHoverSince;

    private ToolTip? _titleToolTip;

    private static readonly TimeSpan TitleToolTipDelay = TimeSpan.FromMilliseconds(400);

    private void InitializeTitleBar()
    {
        ApplyTitleBarIcon();
        ApplyTitleShieldIcon();

        _viewModel.PropertyChanged += OnTitleBarViewModelPropertyChanged;

        _titleHoverTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _titleHoverTimer.Tick += OnTitleHoverTimerTick;
        _titleHoverTimer.Start();

        Closed += (_, _) => StopTitleHoverTimer();
    }

    private void StopTitleHoverTimer()
    {
        CloseTitleToolTip();
        _titleHoverTimer?.Stop();
        _titleHoverTimer = null;
    }

    private void OnTitleBarViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 盾牌 Source 是手写的，不能靠绑定刷新
        if (e.PropertyName == nameof(MainViewModel.ShowTitleShield))
        {
            ApplyTitleShieldIcon();
        }
    }

    // 高 DPI 取更大图层，图标才不发虚
    private void ApplyTitleBarIcon()
    {
        try
        {
            double scale = DpiHelper.ScaleFor(this);
            ImageSource? icon = AppIconProvider.GetTitleBarIcon(scale);
            if (icon is not null && MainTitleBar.Icon is Wpf.Ui.Controls.ImageIcon imageIcon)
            {
                imageIcon.Source = icon;
            }
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(MainWindow), $"设置标题栏图标失败：{ex.Message}");
        }
    }

    private void ApplyTitleShieldIcon()
    {
        try
        {
            // 盾牌在控件模板里，首次调用可能还没应用模板，先展开
            MainTitleBar.ApplyTemplate();
            var shield = MainTitleBar.Template?.FindName("PART_ShieldIcon", MainTitleBar) as Image;
            if (shield is null)
            {
                return;
            }

            if (!_viewModel.ShowTitleShield)
            {
                shield.Source = null;
                return;
            }

            double scale = DpiHelper.ScaleFor(this);
            int pixels = DpiHelper.PhysicalPixels(16, scale);
            shield.Source = SystemIconService.GetShieldIcon(pixels);
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(MainWindow), $"设置标题栏盾牌图标失败：{ex.Message}");
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplyTitleBarIcon();
        ApplyTitleShieldIcon();
    }

    private void OnTitleHoverTimerTick(object? sender, EventArgs e)
    {
        try
        {
            if (_titleToolTip is not null)
            {
                if (!IsMouseOverTitleText())
                {
                    CloseTitleToolTip();
                }

                return;
            }

            if (!IsActive || !IsMouseOverTitleText())
            {
                _titleHoverSince = null;
                return;
            }

            _titleHoverSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - _titleHoverSince.Value >= TitleToolTipDelay)
            {
                OpenTitleToolTip();
            }
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(MainWindow), $"标题栏悬停检测失败：{ex.Message}");
        }
    }

    // 控件不参与命中测试，只能用 DIP 坐标自己算边界
    private bool IsMouseOverTitleText()
    {
        if (TitleTextControl.ActualWidth <= 0 || TitleTextControl.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            Point local = System.Windows.Input.Mouse.GetPosition(TitleTextControl);
            return new Rect(0, 0, TitleTextControl.ActualWidth, TitleTextControl.ActualHeight).Contains(local);
        }
        catch
        {
            return false;
        }
    }

    // 内容是不带 \\?\ 的完整路径
    private void OpenTitleToolTip()
    {
        var toolTip = new ToolTip
        {
            Content = _viewModel.TitleToolTip,
            PlacementTarget = TitleTextControl,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            HorizontalOffset = 0,
            VerticalOffset = 2,
            StaysOpen = true,
        };

        toolTip.Closed += (_, _) => _titleToolTip = null;
        toolTip.IsOpen = true;
        _titleToolTip = toolTip;
    }

    private void CloseTitleToolTip()
    {
        _titleHoverSince = null;

        ToolTip? toolTip = _titleToolTip;
        _titleToolTip = null;
        if (toolTip is not null)
        {
            toolTip.IsOpen = false;
        }
    }
}
