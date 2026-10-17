using ChangJing.App.Core.Settings;
using ChangJing.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChangJing.App.ViewModels;

/// <summary>设置对话框的视图模型</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;

    public SettingsViewModel(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        bool fixedMode = RecycleBinCapacity.IsFixedGigabyteMode(settings.RecycleBinCapacityMode);
        _useFixedCapacity = fixedMode;
        _capacityPercentText = FormatNumber(settings.RecycleBinCapacityPercent);
        _capacityGigabytesText = FormatNumber(settings.RecycleBinCapacityGigabytes);
        _deleteWithoutRecycleBin = settings.DeleteWithoutRecycleBin;
        _useSystemTheme = ThemeService.NormalizeMode(settings.ThemeMode) == AppSettings.ThemeModeSystem;
        _useDarkTheme = ThemeService.NormalizeMode(settings.ThemeMode) == AppSettings.ThemeModeDark;
    }

    /// <summary>「关于」分页的视图模型</summary>
    public AboutViewModel About { get; } = new();

    // 回收站容量

    /// <summary>是否使用「固定大小」</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsePercentCapacity))]
    [NotifyPropertyChangedFor(nameof(PercentEditorEnabled))]
    [NotifyPropertyChangedFor(nameof(FixedEditorEnabled))]
    private bool _useFixedCapacity;

    public bool UsePercentCapacity
    {
        get => !UseFixedCapacity;
        set
        {
            if (value)
            {
                UseFixedCapacity = false;
            }
        }
    }

    [ObservableProperty]
    private string _capacityPercentText = string.Empty;

    [ObservableProperty]
    private string _capacityGigabytesText = string.Empty;

    public bool PercentEditorEnabled => !UseFixedCapacity;

    public bool FixedEditorEnabled => UseFixedCapacity;

    public string PercentRangeHint =>
        $"{AppSettings.MinRecycleBinCapacityPercent:0} ~ {AppSettings.MaxRecycleBinCapacityPercent:0}（0 = 不限制）";

    public string GigabyteRangeHint =>
        $"{AppSettings.MinRecycleBinCapacityGigabytes:0} ~ {AppSettings.MaxRecycleBinCapacityGigabytes:0} GB";

    // 直接永久删除

    /// <summary>删除时不进回收站直接永久删除，每次仍确认</summary>
    [ObservableProperty]
    private bool _deleteWithoutRecycleBin;

    // 主题

    /// <summary>「跟随系统」单选，默认</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseLightTheme))]
    private bool _useSystemTheme = true;

    /// <summary>「浅色」单选</summary>
    public bool UseLightTheme
    {
        get => !UseSystemTheme && !UseDarkTheme;
        set
        {
            if (value)
            {
                UseSystemTheme = false;
                UseDarkTheme = false;
            }
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseLightTheme))]
    private bool _useDarkTheme;

    public string SelectedThemeMode
        => UseDarkTheme
            ? AppSettings.ThemeModeDark
            : UseLightTheme
                ? AppSettings.ThemeModeLight
                : AppSettings.ThemeModeSystem;

    // 校验与保存

    /// <summary>校验提示；为空表示校验通过</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationError))]
    private string _validationError = string.Empty;

    public bool HasValidationError => ValidationError.Length > 0;

    /// <summary>把界面值校验后写回设置，不落盘</summary>
    public bool TryApplyTo(out string error)
    {
        error = string.Empty;

        double percent = _settings.RecycleBinCapacityPercent;
        double gigabytes = _settings.RecycleBinCapacityGigabytes;

        if (UseFixedCapacity)
        {
            if (!TryParseNumber(CapacityGigabytesText, out gigabytes))
            {
                error = "固定大小无效，请输入数字。";
            }
            else if (gigabytes < AppSettings.MinRecycleBinCapacityGigabytes
                     || gigabytes > AppSettings.MaxRecycleBinCapacityGigabytes)
            {
                error = $"固定大小超出允许范围（{GigabyteRangeHint}）。";
            }
        }
        else
        {
            if (!TryParseNumber(CapacityPercentText, out percent))
            {
                error = "百分比无效，请输入数字。";
            }
            else if (percent < AppSettings.MinRecycleBinCapacityPercent
                     || percent > AppSettings.MaxRecycleBinCapacityPercent)
            {
                error = $"百分比超出允许范围（{PercentRangeHint}）。";
            }
        }

        if (error.Length > 0)
        {
            ValidationError = error;
            return false;
        }

        ValidationError = string.Empty;

        _settings.RecycleBinCapacityMode = UseFixedCapacity
            ? AppSettings.CapacityModeFixedGigabyte
            : AppSettings.CapacityModePercent;
        _settings.RecycleBinCapacityPercent = percent;
        _settings.RecycleBinCapacityGigabytes = gigabytes;
        _settings.DeleteWithoutRecycleBin = DeleteWithoutRecycleBin;
        _settings.ThemeMode = SelectedThemeMode;

        // 回显规范化后的数值，例如「10.0」→「10」
        CapacityPercentText = FormatNumber(percent);
        CapacityGigabytesText = FormatNumber(gigabytes);

        return true;
    }

    public void ReloadFromSettings()
    {
        bool fixedMode = RecycleBinCapacity.IsFixedGigabyteMode(_settings.RecycleBinCapacityMode);
        UseFixedCapacity = fixedMode;
        CapacityPercentText = FormatNumber(_settings.RecycleBinCapacityPercent);
        CapacityGigabytesText = FormatNumber(_settings.RecycleBinCapacityGigabytes);
        DeleteWithoutRecycleBin = _settings.DeleteWithoutRecycleBin;

        string mode = ThemeService.NormalizeMode(_settings.ThemeMode);
        UseSystemTheme = mode == AppSettings.ThemeModeSystem;
        UseDarkTheme = mode == AppSettings.ThemeModeDark;

        ValidationError = string.Empty;
    }

    private static bool TryParseNumber(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return double.TryParse(
            text.Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.CurrentCulture,
            out value)
               && !double.IsNaN(value)
               && !double.IsInfinity(value);
    }

    private static string FormatNumber(double value)
        => value.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture);
}
