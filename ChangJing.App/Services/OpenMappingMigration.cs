using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using ChangJing.App.Core.FileSystem;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Services;

/// <summary>迁移：清除旧版本用 DefineDosDevice 建立的残留盘符映射</summary>
// 只有「记录过映射盘符」才移除，绝不乱删用户自己的 subst
public static class OpenMappingMigration
{
    /// <summary>旧版本盘符映射记录文件，JSON 数组含 Letter 与 NtTarget</summary>
    public static string RecordFilePath
        => string.IsNullOrEmpty(RecordOverride) ? DefaultRecordFilePath : RecordOverride!;

    /// <summary>记录文件路径覆盖，仅验证用，产品不设置</summary>
    public static string? RecordOverride { get; set; }

    private static string DefaultRecordFilePath { get; } = Path.Combine(AppPaths.AppDataDirectory, "legacy-dos-mappings.json");

    private sealed class LegacyMapping
    {
        public string? Letter { get; set; }

        public string? NtTarget { get; set; }
    }

    /// <summary>启动时调用：有旧映射记录则逐条移除，没有就跳过，不动用户 subst</summary>
    public static int RemoveLegacyMappings()
    {
        if (!File.Exists(RecordFilePath))
        {
            LogManager.Info(typeof(OpenMappingMigration), "没有旧版盘符映射记录，跳过迁移。");
            return 0;
        }

        List<LegacyMapping>? mappings;
        try
        {
            string json = File.ReadAllText(RecordFilePath);
            mappings = JsonSerializer.Deserialize<List<LegacyMapping>>(json);
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(OpenMappingMigration), $"读取旧映射记录失败：{ex.Message}");
            return 0;
        }

        int removed = 0;
        if (mappings is not null)
        {
            foreach (LegacyMapping mapping in mappings)
            {
                if (string.IsNullOrWhiteSpace(mapping.Letter) || string.IsNullOrWhiteSpace(mapping.NtTarget))
                {
                    continue;
                }

                try
                {
                    if (NativeMethods.DefineDosDeviceW(
                            NativeMethods.DddRemoveDefinition | NativeMethods.DddExactMatchOnRemove,
                            mapping.Letter!,
                            mapping.NtTarget!))
                    {
                        removed++;
                        LogManager.Info(typeof(OpenMappingMigration), $"已移除旧版盘符映射：{mapping.Letter}");
                    }
                    else
                    {
                        int error = Marshal.GetLastWin32Error();
                        LogManager.Debug(
                            typeof(OpenMappingMigration),
                            $"移除旧版盘符映射失败（{mapping.Letter}，错误码 {error}）—— 可能已不存在。");
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Warning(typeof(OpenMappingMigration), $"移除旧版盘符映射异常：{ex.Message}");
                }
            }
        }

        // 记录文件已处理：删除，避免每次启动重复尝试
        try
        {
            File.Delete(RecordFilePath);
        }
        catch (Exception ex)
        {
            LogManager.Debug(typeof(OpenMappingMigration), $"删除旧映射记录文件失败：{ex.Message}");
        }

        return removed;
    }
}
