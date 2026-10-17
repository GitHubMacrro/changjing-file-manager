using System.IO;
using System.Text.Json;
using ChangJing.App.Services;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Core.Settings;

/// <summary>设置读写：加载 / 原子保存 settings.json，读失败回退默认值并记日志</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _filePath;

    public SettingsService()
        : this(AppPaths.SettingsFilePath)
    {
    }

    public SettingsService(string filePath)
    {
        _filePath = filePath;
    }

    /// <summary>当前设置实例；构造后始终非 null</summary>
    public AppSettings Current { get; private set; } = new();

    /// <summary>加载设置，文件缺失或解析失败时用默认值并记日志，不抛出</summary>
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                LogManager.Info(typeof(SettingsService), $"设置文件不存在，使用默认值：{_filePath}");
                Current = new AppSettings();
                return Current;
            }

            string json = File.ReadAllText(_filePath);
            AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
            if (loaded is null)
            {
                LogManager.Warning(typeof(SettingsService), $"设置文件内容为空，使用默认值：{_filePath}");
                Current = new AppSettings();
                return Current;
            }

            // 对不合理的数值做兜底
            Sanitize(loaded);
            Current = loaded;
            LogManager.Info(typeof(SettingsService), $"设置已加载：{_filePath}");
            return Current;
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(SettingsService), ex);
            LogManager.Warning(typeof(SettingsService), $"读取设置失败，已回退默认值：{_filePath}");
            Current = new AppSettings();
            return Current;
        }
    }

    /// <summary>原子保存当前设置：先写同目录临时文件，成功后 File.Replace 或 Move 覆盖</summary>
    public bool Save()
    {
        return Save(Current);
    }

    /// <summary>原子保存指定设置对象，并将其设为 Current</summary>
    public bool Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Current = settings;

        string? directory = Path.GetDirectoryName(_filePath);
        string tempPath = _filePath + ".tmp";

        try
        {
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(settings, SerializerOptions);

            // 先写临时文件并落盘，保证临时文件内容完整
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            // 原子替换目标文件
            if (File.Exists(_filePath))
            {
                File.Replace(tempPath, _filePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, _filePath);
            }

            return true;
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(SettingsService), ex);
            TryDeleteTemp(tempPath);
            return false;
        }
    }

    private static void TryDeleteTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(SettingsService), $"清理临时文件失败：{tempPath} -> {ex.Message}");
        }
    }

    private static void Sanitize(AppSettings settings)
    {
        settings.NavigationWidth = Clamp(settings.NavigationWidth, AppSettings.DefaultNavigationWidth, min: 120);
        settings.PreviewPaneWidth = Clamp(settings.PreviewPaneWidth, AppSettings.DefaultPreviewPaneWidth, min: 160);

        WindowPlacement window = settings.Window ??= new WindowPlacement();
        window.Width = Clamp(window.Width, AppSettings.DefaultWindowWidth, min: 960);
        window.Height = Clamp(window.Height, AppSettings.DefaultWindowHeight, min: 600);

        if (!IsFinite(window.Left) || !IsFinite(window.Top))
        {
            window.Left = 0;
            window.Top = 0;
            window.HasValue = false;
        }

        settings.RecentTargets = SanitizeRecentTargets(settings.RecentTargets);

        settings.ViewMode = SanitizeViewMode(settings.ViewMode);
        settings.IconSize = Math.Clamp(settings.IconSize, AppSettings.MinIconSize, AppSettings.MaxIconSize);

        settings.FolderViews = SanitizeFolderViews(settings.FolderViews);
        settings.DetailsColumnWidths = SanitizeColumnWidths(settings.DetailsColumnWidths);

        settings.RecycleBinSortColumn = SanitizeRecycleBinSortColumn(settings.RecycleBinSortColumn);
        settings.SearchSortColumn = SanitizeSearchSortColumn(settings.SearchSortColumn);
        settings.SearchHistory = SanitizeSearchHistory(settings.SearchHistory);

        settings.RecycleBinCapacityMode = SanitizeCapacityMode(settings.RecycleBinCapacityMode);

        settings.RecycleBinCapacityPercent = Math.Clamp(
            IsFinite(settings.RecycleBinCapacityPercent)
                ? settings.RecycleBinCapacityPercent
                : AppSettings.DefaultRecycleBinCapacityPercent,
            AppSettings.MinRecycleBinCapacityPercent,
            AppSettings.MaxRecycleBinCapacityPercent);

        settings.RecycleBinCapacityGigabytes = Math.Clamp(
            IsFinite(settings.RecycleBinCapacityGigabytes)
                ? settings.RecycleBinCapacityGigabytes
                : AppSettings.DefaultRecycleBinCapacityGigabytes,
            AppSettings.MinRecycleBinCapacityGigabytes,
            AppSettings.MaxRecycleBinCapacityGigabytes);

        settings.ThemeMode = SanitizeThemeMode(settings.ThemeMode);
    }

    private static string SanitizeThemeMode(string? mode)
        => mode switch
        {
            AppSettings.ThemeModeLight => AppSettings.ThemeModeLight,
            AppSettings.ThemeModeDark => AppSettings.ThemeModeDark,
            _ => AppSettings.ThemeModeSystem,
        };

    private static string SanitizeCapacityMode(string? mode)
    {
        return string.Equals(mode, AppSettings.CapacityModeFixedGigabyte, StringComparison.OrdinalIgnoreCase)
            ? AppSettings.CapacityModeFixedGigabyte
            : AppSettings.CapacityModePercent;
    }

    private static List<FolderViewState> SanitizeFolderViews(List<FolderViewState>? states)
    {
        var result = new List<FolderViewState>();
        if (states is null)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FolderViewState? state in states)
        {
            if (state is null || string.IsNullOrWhiteSpace(state.Path) || !seen.Add(state.Path))
            {
                continue;
            }

            state.ViewMode = SanitizeViewMode(state.ViewMode);
            state.IconSize = Math.Clamp(state.IconSize, AppSettings.MinIconSize, AppSettings.MaxIconSize);
            state.SortColumn = SanitizeSortColumn(state.SortColumn);
            result.Add(state);
        }

        if (result.Count <= AppSettings.MaxFolderViews)
        {
            return result;
        }

        // 超出上限：保留最近使用的若干条
        return result
            .OrderByDescending(state => state.LastUsedTicks)
            .Take(AppSettings.MaxFolderViews)
            .ToList();
    }

    private static Dictionary<string, double> SanitizeColumnWidths(Dictionary<string, double>? widths)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (widths is null)
        {
            return result;
        }

        foreach (KeyValuePair<string, double> pair in widths)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || !IsFinite(pair.Value) || pair.Value < 24)
            {
                continue;
            }

            string key = MigrateColumnWidthKey(pair.Key.Trim());
            if (key.Length == 0)
            {
                continue;
            }

            result[key] = Math.Min(pair.Value, 4000);
        }

        return result;
    }

    private static string MigrateColumnWidthKey(string key)
    {
        // 旧版裸键 → files.*
        if (Array.IndexOf(LegacyColumnWidthKeys, key) >= 0)
        {
            return AppSettings.ColumnWidthFilesPrefix + key;
        }

        foreach (string prefix in ColumnWidthPrefixes)
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string column = key[prefix.Length..];
            if (Array.IndexOf(ColumnWidthColumnNames, column) >= 0)
            {
                return prefix + column;
            }

            return string.Empty;
        }

        return string.Empty;
    }

    private static readonly string[] LegacyColumnWidthKeys = { "Name", "DateModified", "Type", "Size" };

    private static readonly string[] ColumnWidthPrefixes =
    {
        AppSettings.ColumnWidthFilesPrefix,
        AppSettings.ColumnWidthSearchPrefix,
        AppSettings.ColumnWidthRecyclePrefix,
    };

    private static readonly string[] ColumnWidthColumnNames =
    {
        "Name", "Folder", "DateModified", "Type", "Size",
        "OriginalLocation", "DeletedDate",
    };

    private static List<string> SanitizeSearchHistory(List<string>? history)
    {
        var result = new List<string>();
        if (history is null)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (string? item in history)
        {
            if (string.IsNullOrWhiteSpace(item))
            {
                continue;
            }

            string trimmed = item!.Trim();
            if (!seen.Add(trimmed))
            {
                continue;
            }

            result.Add(trimmed);
            if (result.Count >= AppSettings.MaxSearchHistory)
            {
                break;
            }
        }

        return result;
    }

    private static string SanitizeRecycleBinSortColumn(string? column)
    {
        if (!string.IsNullOrWhiteSpace(column))
        {
            foreach (string name in RecycleBinSortColumnNames)
            {
                if (string.Equals(name, column, StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }
        }

        return AppSettings.DefaultRecycleBinSortColumn;
    }

    private static readonly string[] RecycleBinSortColumnNames =
    {
        "Name", "OriginalLocation", "DeletedDate", "Size", "Type",
    };

    private static string SanitizeSearchSortColumn(string? column)
    {
        if (!string.IsNullOrWhiteSpace(column))
        {
            foreach (string name in SearchSortColumnNames)
            {
                if (string.Equals(name, column, StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }
        }

        return AppSettings.DefaultSortColumn;
    }

    private static readonly string[] SearchSortColumnNames =
    {
        "Name", "Folder", "DateModified", "Type", "Size",
    };

    private static string SanitizeSortColumn(string? column)
    {
        if (!string.IsNullOrWhiteSpace(column))
        {
            foreach (string name in SortColumnNames)
            {
                if (string.Equals(name, column, StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }
        }

        return AppSettings.DefaultSortColumn;
    }

    private static readonly string[] SortColumnNames = { "Name", "DateModified", "Type", "Size" };

    private static string SanitizeViewMode(string? viewMode)
    {
        if (!string.IsNullOrWhiteSpace(viewMode))
        {
            foreach (string name in FileViewModeNames)
            {
                if (string.Equals(name, viewMode, StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }
        }

        return AppSettings.DefaultViewMode;
    }

    private static readonly string[] FileViewModeNames =
    {
        "ExtraLargeIcons", "LargeIcons", "MediumIcons", "SmallIcons",
        "List", "Details", "Tiles", "Content",
    };

    private static List<string> SanitizeRecentTargets(List<string>? targets)
    {
        var result = new List<string>();
        if (targets is null)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? target in targets)
        {
            if (string.IsNullOrWhiteSpace(target) || !seen.Add(target!))
            {
                continue;
            }

            result.Add(target!);
            if (result.Count >= AppSettings.MaxRecentTargets)
            {
                break;
            }
        }

        return result;
    }

    private static double Clamp(double value, double fallback, double min)
    {
        if (!IsFinite(value) || value < min)
        {
            return fallback;
        }

        return value;
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
