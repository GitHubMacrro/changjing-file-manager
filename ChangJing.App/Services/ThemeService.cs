using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CustomWin.Utils.Logs;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ChangJing.App.Services;

/// <summary>主题与背景服务：跟随系统深浅色，并切换畅径调色板</summary>
// 关闭 Mica / Acrylic 等背景特效（Win10 上 Mica 无效、Acrylic 不稳定），窗口用畅径底色
public sealed class ThemeService
{
    // 不再使用任何背景特效，窗口用不透明底色
    private const WindowBackdropType PreferredBackdrop = WindowBackdropType.None;

    // 畅径调色板文件（绝对 pack URI）：程序集名为 ChangJing，根命名空间不同
    private const string PaletteLightSource = "pack://application:,,,/ChangJing;component/Themes/ChangJingTheme.Light.xaml";
    private const string PaletteDarkSource = "pack://application:,,,/ChangJing;component/Themes/ChangJingTheme.Dark.xaml";

    // 被本服务跟踪主题的窗口（用于切换「跟随系统 / 固定深浅色」时挂接或解除挂接）
    private readonly List<WeakReference<Window>> _watchedWindows = new();

    // 当前主题模式：System（跟随系统）/ Light / Dark
    private string _mode = Core.Settings.AppSettings.ThemeModeSystem;

    private bool _subscribed;

    public ApplicationTheme CurrentTheme { get; private set; } = ApplicationTheme.Light;

    public string ThemeMode => _mode;

    /// <summary>当前背景类型：恒为 WindowBackdropType.None</summary>
    public WindowBackdropType EffectiveBackdrop { get; private set; } = WindowBackdropType.None;

    /// <summary>启动时按设置的主题模式初始化主题与调色板</summary>
    public void Initialize(string themeMode)
    {
        _mode = NormalizeMode(themeMode);
        EffectiveBackdrop = PreferredBackdrop;
        VerifyPalettes();
        ApplyMode();
    }

    /// <summary>启动自检：两份调色板键集须一致且关键键齐全</summary>
    // 某个主题缺键会回退到 WPF-UI 默认值，深色下就会出现亮底 / 黑底等异常
    private static void VerifyPalettes()
    {
        try
        {
            HashSet<object> light = LoadPaletteKeys(PaletteLightSource);
            HashSet<object> dark = LoadPaletteKeys(PaletteDarkSource);
            if (light.Count == 0 || dark.Count == 0)
            {
                LogManager.Warning(typeof(ThemeService), "调色板加载失败，跳过键集自检。");
                return;
            }

            foreach (object key in light.Except(dark).ToList())
            {
                LogManager.Warning(typeof(ThemeService), $"深色调色板缺少键：{key}（浅色中存在）。");
            }

            foreach (object key in dark.Except(light).ToList())
            {
                LogManager.Warning(typeof(ThemeService), $"浅色调色板缺少键：{key}（深色中存在）。");
            }

            foreach (string required in RequiredPaletteKeys)
            {
                if (!light.Contains(required) || !dark.Contains(required))
                {
                    LogManager.Warning(typeof(ThemeService), $"调色板缺少必需键：{required}。");
                }
            }
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(ThemeService), $"调色板自检失败：{ex.Message}");
        }
    }

    private static HashSet<object> LoadPaletteKeys(string source)
    {
        var dictionary = new ResourceDictionary { Source = new Uri(source, UriKind.Absolute) };
        var keys = new HashSet<object>();
        foreach (object key in dictionary.Keys)
        {
            keys.Add(key);
        }

        return keys;
    }

    // 既有畅径自有键，也有刻意覆盖的 WPF-UI 键；缺任何一个都会让某控件退回默认色
    private static readonly string[] RequiredPaletteKeys =
    {
        "CjWindowBackgroundBrush", "CjCardBackgroundBrush", "CjCardBorderBrush",
        "CjTextPrimaryBrush", "CjTextSecondaryBrush", "CjSelectionBackgroundBrush",
        "CjHoverBackgroundBrush", "CjAccentBrush", "CjDangerBrush", "CjBadgeForegroundBrush",
        "CjInputBackgroundBrush", "CjHeaderBackgroundBrush", "CjPanelDividerBrush",
        "CjDriveProgressBrush", "CjDriveProgressLowBrush", "CjDriveProgressTrackBrush",
        "TextFillColorPrimaryBrush", "TextFillColorSecondaryBrush", "ApplicationBackgroundBrush",
        "CardBackgroundFillColorDefaultBrush", "ControlFillColorDefaultBrush",
        "TextControlBackground", "TextControlForeground", "ButtonForeground",
        "ContextMenuBackground", "ContextMenuForeground", "ToolTipBackground", "ToolTipForeground",
        "ListViewItemForeground", "TreeViewItemBackgroundSelected", "MenuBarBackground",
        "ListBoxItemSelectedBackgroundThemeBrush", "ListBoxItemSelectedForegroundThemeBrush",
        "ProgressBarForeground", "SliderThumbBackground", "AccentButtonBackground",
    };

    /// <summary>切换主题模式：System 时挂接系统跟随，其余固定该主题</summary>
    public void SetThemeMode(string themeMode)
    {
        string normalized = NormalizeMode(themeMode);
        if (string.Equals(normalized, _mode, StringComparison.Ordinal))
        {
            return;
        }

        _mode = normalized;
        ApplyMode();
    }

    /// <summary>把主题模式字符串规范化为已知值；坏值回退「跟随系统」</summary>
    public static string NormalizeMode(string? themeMode)
        => themeMode switch
        {
            Core.Settings.AppSettings.ThemeModeLight => Core.Settings.AppSettings.ThemeModeLight,
            Core.Settings.AppSettings.ThemeModeDark => Core.Settings.AppSettings.ThemeModeDark,
            _ => Core.Settings.AppSettings.ThemeModeSystem,
        };

    private void ApplyMode()
    {
        bool followSystem = string.Equals(_mode, Core.Settings.AppSettings.ThemeModeSystem, StringComparison.Ordinal);

        try
        {
            // 跟随系统：订阅系统主题变化；固定深浅色：解除订阅（避免系统切换覆盖用户选择）
            if (followSystem && !_subscribed)
            {
                ApplicationThemeManager.Changed += OnApplicationThemeChanged;
                _subscribed = true;
            }
            else if (!followSystem && _subscribed)
            {
                ApplicationThemeManager.Changed -= OnApplicationThemeChanged;
                _subscribed = false;
            }

            CurrentTheme = followSystem
                ? SystemThemeToAppTheme()
                : string.Equals(_mode, Core.Settings.AppSettings.ThemeModeDark, StringComparison.Ordinal)
                    ? ApplicationTheme.Dark
                    : ApplicationTheme.Light;

            ApplicationThemeManager.Apply(CurrentTheme, EffectiveBackdrop, updateAccent: true);
            ApplyPalette(CurrentTheme);
            ApplyWindowWatching(followSystem);
            ScheduleDeferredRefresh();

            LogManager.Info(
                typeof(ThemeService),
                $"主题已应用：Mode={_mode}, Theme={CurrentTheme}, Backdrop={EffectiveBackdrop}, " +
                $"SystemTheme={ApplicationThemeManager.GetSystemTheme()}");
        }
        catch (Exception ex)
        {
            // 主题应用失败不应阻断启动：回退为不透明主题
            LogManager.Error(typeof(ThemeService), ex);
            try
            {
                EffectiveBackdrop = WindowBackdropType.None;
                ApplicationThemeManager.Apply(CurrentTheme, WindowBackdropType.None, updateAccent: false);
                ApplyPalette(CurrentTheme);
            }
            catch (Exception inner)
            {
                LogManager.Error(typeof(ThemeService), inner);
            }
        }
    }

    private static ApplicationTheme SystemThemeToAppTheme()
        => ApplicationThemeManager.GetSystemTheme() switch
        {
            SystemTheme.Dark or SystemTheme.HCBlack => ApplicationTheme.Dark,
            SystemTheme.Light or SystemTheme.HCWhite => ApplicationTheme.Light,
            _ => ApplicationThemeManager.GetAppTheme(),
        };

    // 系统 / 应用主题变化时切换调色板（仅在「跟随系统」模式下订阅）
    private void OnApplicationThemeChanged(ApplicationTheme theme, Color accent)
    {
        try
        {
            CurrentTheme = theme;
            ApplyPalette(theme);
            ScheduleDeferredRefresh();
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(ThemeService), ex);
        }
    }

    /// <summary>主题切换完成后于 ApplicationIdle 再「补一次」调色板与窗口背景</summary>
    // FluentWindow 会用命令式方式把 Window.Background 覆盖成 WPF-UI 底色，盖掉 XAML 绑定；之后重设回来
    private void ScheduleDeferredRefresh()
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            ReassertWindowBackgrounds();
            return;
        }

        // 低优先级，排在 WPF-UI 的同步 / 微任务主题覆盖之后
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(ReassertWindowBackgrounds));
    }

    // 把所有已跟踪窗口（及主窗口 / 当前打开窗口）的背景重新绑回畅径底色
    private void ReassertWindowBackgrounds()
    {
        try
        {
            foreach (Window window in EnumerateLiveWindows())
            {
                ReassertWindowBackground(window);
            }
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(ThemeService), ex);
        }
    }

    // 把单个窗口的背景重新绑定到 CjWindowBackgroundBrush（覆盖 WPF-UI 的命令式底色）
    private static void ReassertWindowBackground(Window window)
    {
        try
        {
            window.SetResourceReference(Window.BackgroundProperty, "CjWindowBackgroundBrush");
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(ThemeService), $"重设窗口背景失败（{window.GetType().Name}）：{ex.Message}");
        }
    }

    // 枚举已跟踪窗口 + 主窗口 + 当前所有已打开窗口，去重
    private IEnumerable<Window> EnumerateLiveWindows()
    {
        var seen = new HashSet<Window>();

        foreach (WeakReference<Window> reference in _watchedWindows.ToList())
        {
            if (reference.TryGetTarget(out Window? window) && seen.Add(window))
            {
                yield return window;
            }
        }

        if (Application.Current is { } app)
        {
            if (app.MainWindow is { } main && seen.Add(main))
            {
                yield return main;
            }

            foreach (Window window in app.Windows)
            {
                if (seen.Add(window))
                {
                    yield return window;
                }
            }
        }
    }

    // 必须是顶层合并字典：WPF 只在顶层槽被替换时才通知已加载元素上的 DynamicResource
    public static void ApplyPalette(ApplicationTheme theme)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        string targetSource = theme == ApplicationTheme.Dark ? PaletteDarkSource : PaletteLightSource;

        int index = FindPaletteIndex(app.Resources);
        var replacement = new ResourceDictionary { Source = new Uri(targetSource, UriKind.Absolute) };

        if (index >= 0)
        {
            // 顶层槽替换（保持位置）——这是唯一能通知已加载 DynamicResource 消费者的方式
            app.Resources.MergedDictionaries[index] = replacement;
        }
        else
        {
            // 兜底：启动早期 / 异常情况下找不到槽，直接追加到最后（优先于 WPF-UI）
            app.Resources.MergedDictionaries.Add(replacement);
            LogManager.Warning(typeof(ThemeService), $"未找到畅径调色板槽，已追加：{targetSource}");
        }
    }

    // 在应用资源的顶层合并字典里定位畅径调色板（Light / Dark）的槽索引；找不到返回 -1
    private static int FindPaletteIndex(ResourceDictionary root)
    {
        for (int i = 0; i < root.MergedDictionaries.Count; i++)
        {
            string? source = root.MergedDictionaries[i].Source?.OriginalString;
            if (source is null)
            {
                continue;
            }

            if (source.EndsWith("ChangJingTheme.Light.xaml", StringComparison.OrdinalIgnoreCase)
                || source.EndsWith("ChangJingTheme.Dark.xaml", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>让指定窗口跟随系统主题变化；仅在「跟随系统」模式下生效</summary>
    public void WatchWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        // 记录窗口，便于后续从「跟随系统」切到固定深浅色时解除挂接
        _watchedWindows.RemoveAll(reference => !reference.TryGetTarget(out Window? target) || ReferenceEquals(target, window));
        _watchedWindows.Add(new WeakReference<Window>(window));

        // 无论哪种模式，都在窗口显示后把背景补回畅径底色（WPF-UI 会在显示时命令式覆盖）
        ScheduleDeferredRefresh();

        if (!string.Equals(_mode, Core.Settings.AppSettings.ThemeModeSystem, StringComparison.Ordinal))
        {
            return;
        }

        WatchCore(window);
    }

    private void WatchCore(Window window)
    {
        try
        {
            SystemThemeWatcher.Watch(window, EffectiveBackdrop, updateAccents: true);
            LogManager.Info(typeof(ThemeService), $"已开始跟随系统主题：{window.GetType().Name}");
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(ThemeService), ex);
        }
    }

    public void UnwatchWindow(Window window)
    {
        if (window is null)
        {
            return;
        }

        _watchedWindows.RemoveAll(reference => !reference.TryGetTarget(out Window? target) || ReferenceEquals(target, window));
        UnwatchCore(window);
    }

    private void UnwatchCore(Window window)
    {
        try
        {
            SystemThemeWatcher.UnWatch(window);
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(ThemeService), $"取消跟随系统主题失败：{ex.Message}");
        }
    }

    // 按当前模式对已跟踪窗口挂接 / 解除系统主题跟随（模式切换时调用）
    private void ApplyWindowWatching(bool followSystem)
    {
        foreach (WeakReference<Window> reference in _watchedWindows.ToList())
        {
            if (!reference.TryGetTarget(out Window? window))
            {
                continue;
            }

            if (followSystem)
            {
                WatchCore(window);
            }
            else
            {
                UnwatchCore(window);
            }
        }
    }

    /// <summary>供 XAML 绑定的当前应用背景色</summary>
    public static Color GetApplicationBackgroundColor()
    {
        if (Application.Current?.TryFindResource("ApplicationBackgroundBrush") is SolidColorBrush brush)
        {
            return brush.Color;
        }

        return Colors.Transparent;
    }
}
