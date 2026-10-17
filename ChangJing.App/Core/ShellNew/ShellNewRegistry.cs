using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using CustomWin.Utils.Logs;
using Microsoft.Win32;

namespace ChangJing.App.Core.ShellNew;

/// <summary>从注册表枚举「新建」类型 ShellNew，供右键新建子菜单使用</summary>
// 数据源：HKCR\<ext>\ShellNew 与 HKCU 用户覆盖，后者优先
public static class ShellNewRegistry
{
    private const string UserClassesPath = @"Software\Classes";

    private static readonly object SyncRoot = new();

    private static readonly object StartLock = new();

    // 必须排除 .lnk：畅径自己生成快捷方式，注册表会解析出同名项
    private static readonly HashSet<string> ExtensionBlocklist =
        new(StringComparer.OrdinalIgnoreCase) { ".lnk" };

    private static volatile IReadOnlyList<ShellNewEntry> _entries = Array.Empty<ShellNewEntry>();

    private static bool _started;

    private static Task? _loadTask;

    /// <summary>最近一次加载的快照，未加载时为空</summary>
    public static IReadOnlyList<ShellNewEntry> Entries => _entries;

    /// <summary>同步完整枚举并刷新 Entries，失败时返回空结果并记日志</summary>
    public static ShellNewEnumerationResult Load()
    {
        ShellNewEnumerationResult result;
        try
        {
            result = Enumerate();
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(ShellNewRegistry), $"枚举「新建」类型失败：{ex.Message}");
            result = new ShellNewEnumerationResult();
        }

        lock (SyncRoot)
        {
            _entries = result.Entries;
        }

        return result;
    }

    /// <summary>幂等：首次调用后台加载，后续立即返回</summary>
    public static void EnsureLoadedInBackground()
    {
        lock (StartLock)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _loadTask = Task.Run(Load);
        }
    }

    /// <summary>等待后台加载完成，未开始则先开始</summary>
    public static Task EnsureLoadedAsync()
    {
        lock (StartLock)
        {
            if (!_started)
            {
                _started = true;
                _loadTask = Task.Run(Load);
            }

            return _loadTask ?? Task.CompletedTask;
        }
    }

    /// <summary>解析 @%SystemRoot%\system32\shell32.dll,-12345 间接字符串，非 @ 开头原样返回</summary>
    public static string ResolveIndirectString(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        if (value[0] != '@')
        {
            return value;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            int hr = ShellNewNativeMethods.SHLoadIndirectString(value, buffer, buffer.Capacity, IntPtr.Zero);
            if (hr == 0 && buffer.Length > 0)
            {
                return buffer.ToString();
            }
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(ShellNewRegistry), $"解析间接字符串失败（{value}）：{ex.Message}");
        }

        int comma = value.LastIndexOf(',');
        string fallback = comma > 0 ? value.Substring(0, comma) : value;
        return fallback.TrimStart('@');
    }

    private static ShellNewEnumerationResult Enumerate()
    {
        List<string> extensionNames = CollectExtensions();
        var entries = new List<ShellNewEntry>();
        int skippedCommands = 0;
        int skippedHandlers = 0;

        foreach (string extension in extensionNames)
        {
            if (ExtensionBlocklist.Contains(extension))
            {
                continue;
            }

            try
            {
                ShellNewEntry? entry = BuildEntry(extension, out bool commandSkipped, out bool handlerSkipped);
                if (commandSkipped)
                {
                    skippedCommands++;
                }

                if (handlerSkipped)
                {
                    skippedHandlers++;
                }

                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
            catch (Exception ex)
            {
                LogManager.Warning(typeof(ShellNewRegistry), $"读取扩展名 {extension} 的 ShellNew 失败：{ex.Message}");
            }
        }

        entries.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCulture));

        return new ShellNewEnumerationResult
        {
            Entries = entries,
            SkippedCommandTypes = skippedCommands,
            SkippedHandlerTypes = skippedHandlers,
        };
    }

    private static List<string> CollectExtensions()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddFrom(RegistryKey root, string subPath)
        {
            try
            {
                // 不 dispose root，可能是共享的静态基键
                if (string.IsNullOrEmpty(subPath))
                {
                    foreach (string name in root.GetSubKeyNames())
                    {
                        if (name.Length > 1 && name[0] == '.')
                        {
                            set.Add(name);
                        }
                    }

                    return;
                }

                using RegistryKey? key = root.OpenSubKey(subPath);
                if (key is null)
                {
                    return;
                }

                foreach (string name in key.GetSubKeyNames())
                {
                    if (name.Length > 1 && name[0] == '.')
                    {
                        set.Add(name);
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Warning(typeof(ShellNewRegistry), $"枚举 {subPath} 下的扩展名失败：{ex.Message}");
            }
        }

        AddFrom(Registry.ClassesRoot, string.Empty);
        AddFrom(Registry.CurrentUser, UserClassesPath);

        var list = new List<string>(set);
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    private static ShellNewEntry? BuildEntry(string extension, out bool commandSkipped, out bool handlerSkipped)
    {
        commandSkipped = false;
        handlerSkipped = false;

        // ProgID 可能承载 ShellNew，如 .zip → CompressedFolder，先解析出来
        string? progId = ResolveProgId(extension);

        RegistryKey? shellNew = OpenUserShellNew(extension);
        try
        {
            // 查找顺序用户覆盖优先：HKCU ext、HKCU ProgID、HKCR ext、HKCR ProgID 的 ShellNew
            shellNew ??= OpenUserShellNew(extension, progId);
            shellNew ??= Registry.ClassesRoot.OpenSubKey(extension + @"\ShellNew");
            shellNew ??= progId is null ? null : Registry.ClassesRoot.OpenSubKey(extension + "\\" + progId + @"\ShellNew");

            if (shellNew is null)
            {
                return null;
            }

            // Command 类型由处理器创建，本工具不支持，跳过并计数
            if (shellNew.GetValue("Command") is string command && command.Length > 0)
            {
                commandSkipped = true;
                return null;
            }

            // 自定义 Handler 创建逻辑在处理器里，照 NullFile 会写出打不开的空壳，跳过
            if (shellNew.GetValue("Handler") is string handler && handler.Length > 0)
            {
                handlerSkipped = true;
                return null;
            }

            bool hasNull = shellNew.GetValue("NullFile") is not null;
            string? fileName = shellNew.GetValue("FileName") as string;
            byte[]? data = shellNew.GetValue("Data") as byte[];

            // 少数系统如本机 .rtf 把 Data 记成 REG_SZ，内容即初始文本，一并支持
            if (data is null && shellNew.GetValue("Data") is string dataText)
            {
                data = Encoding.UTF8.GetBytes(dataText);
            }

            ShellNewKind kind;
            string? template = null;
            byte[]? payload = null;

            if (hasNull)
            {
                kind = ShellNewKind.NullFile;
            }
            else if (!string.IsNullOrEmpty(fileName))
            {
                template = ResolveTemplate(fileName!, out bool resolved);
                if (resolved)
                {
                    kind = ShellNewKind.FileName;
                }
                else
                {
                    // 模板找不到就降级为空文件，不丢弃该类型
                    kind = ShellNewKind.NullFile;
                    template = null;
                }
            }
            else if (data is not null)
            {
                kind = ShellNewKind.Data;
                payload = data;
            }
            else
            {
                return null;
            }

            string displayName = ResolveDisplayName(extension, progId);
            ResolveIcon(progId, out string? iconPath, out int iconIndex);

            return new ShellNewEntry
            {
                Extension = extension,
                ProgId = progId,
                DisplayName = displayName,
                Kind = kind,
                TemplatePath = template,
                Data = payload,
                IconPath = iconPath,
                IconIndex = iconIndex,
            };
        }
        finally
        {
            shellNew?.Dispose();
        }
    }

    private static RegistryKey? OpenUserShellNew(string extension)
    {
        try
        {
            return Registry.CurrentUser.OpenSubKey(UserClassesPath + "\\" + extension + @"\ShellNew");
        }
        catch
        {
            return null;
        }
    }

    private static RegistryKey? OpenUserShellNew(string extension, string? progId)
    {
        if (string.IsNullOrEmpty(progId))
        {
            return null;
        }

        try
        {
            return Registry.CurrentUser.OpenSubKey(UserClassesPath + "\\" + extension + "\\" + progId + @"\ShellNew");
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveProgId(string extension)
    {
        string? progId = ReadDefaultValue(Registry.CurrentUser, UserClassesPath + "\\" + extension);
        if (string.IsNullOrWhiteSpace(progId))
        {
            progId = ReadDefaultValue(Registry.ClassesRoot, extension);
        }

        return string.IsNullOrWhiteSpace(progId) ? null : progId!.Trim();
    }

    private static string ResolveDisplayName(string extension, string? progId)
    {
        string display = string.Empty;

        if (!string.IsNullOrEmpty(progId))
        {
            try
            {
                using RegistryKey? progKey = Registry.ClassesRoot.OpenSubKey(progId!);
                if (progKey is not null)
                {
                    if (progKey.GetValue("FriendlyTypeName") is string friendly && !string.IsNullOrWhiteSpace(friendly))
                    {
                        display = ResolveIndirectString(friendly).Trim();
                    }
                    else if (progKey.GetValue(null) is string defaultValue && !string.IsNullOrWhiteSpace(defaultValue))
                    {
                        display = ResolveIndirectString(defaultValue).Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Warning(typeof(ShellNewRegistry), $"读取 ProgID {progId} 的显示名失败：{ex.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(display))
        {
            string? extDefault = ReadDefaultValue(Registry.ClassesRoot, extension);
            // 扩展名默认值通常是 ProgID，已解析过，避免直接当显示名
            if (!string.IsNullOrWhiteSpace(extDefault)
                && !string.Equals(extDefault, progId, StringComparison.OrdinalIgnoreCase))
            {
                display = ResolveIndirectString(extDefault!).Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(display))
        {
            display = extension.TrimStart('.').ToUpperInvariant() + " 文件";
        }

        return display;
    }

    private static void ResolveIcon(string? progId, out string? iconPath, out int iconIndex)
    {
        iconPath = null;
        iconIndex = 0;

        if (string.IsNullOrEmpty(progId))
        {
            return;
        }

        try
        {
            using RegistryKey? iconKey = Registry.ClassesRoot.OpenSubKey(progId + @"\DefaultIcon");
            if (iconKey?.GetValue(null) is not string raw || string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            string expanded = Environment.ExpandEnvironmentVariables(raw).Trim();
            int comma = expanded.LastIndexOf(',');
            if (comma >= 0)
            {
                string path = expanded.Substring(0, comma).Trim().Trim('"');
                string indexText = expanded.Substring(comma + 1).Trim();
                if (path.Length > 0)
                {
                    iconPath = path;
                }

                if (int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                {
                    iconIndex = index;
                }
            }
            else
            {
                string path = expanded.Trim('"');
                if (path.Length > 0)
                {
                    iconPath = path;
                }
            }
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(ShellNewRegistry), $"读取 ProgID {progId} 的图标失败：{ex.Message}");
        }
    }

    private static string? ReadDefaultValue(RegistryKey root, string subKeyPath)
    {
        try
        {
            using RegistryKey? key = root.OpenSubKey(subKeyPath);
            return key?.GetValue(null) as string;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveTemplate(string value, out bool resolved)
    {
        resolved = false;

        bool looksAbsolute = value.IndexOf('\\') >= 0 || value.IndexOf(':') >= 0;
        if (looksAbsolute)
        {
            string expanded;
            try
            {
                expanded = Environment.ExpandEnvironmentVariables(value);
            }
            catch
            {
                expanded = value;
            }

            if (File.Exists(expanded))
            {
                resolved = true;
                return expanded;
            }

            return null;
        }

        foreach (string directory in GetTemplateDirectories())
        {
            try
            {
                string candidate = Path.Combine(directory, value);
                if (File.Exists(candidate))
                {
                    resolved = true;
                    return candidate;
                }
            }
            catch
            {
                // 忽略单个目录异常，继续下一个
            }
        }

        return null;
    }

    private static IEnumerable<string> GetTemplateDirectories()
    {
        yield return CombineEnv("WINDIR", "ShellNew");
        yield return CombineEnv("USERPROFILE", "Templates");
        yield return CombineEnv("APPDATA", Path.Combine("Microsoft", "Windows", "Templates"));
        yield return CombineEnv("ProgramData", Path.Combine("Microsoft", "Windows", "Templates"));
    }

    private static string CombineEnv(string variable, string relative)
    {
        string? root = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(root))
        {
            return string.Empty;
        }

        try
        {
            return Path.Combine(root!, relative);
        }
        catch
        {
            return string.Empty;
        }
    }
}
