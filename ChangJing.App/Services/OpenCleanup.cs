using CustomWin.Utils.Logs;

namespace ChangJing.App.Services;

/// <summary>外部打开的统一清理 / 清扫入口：短根联接 + 临时副本</summary>
// 清理本次建立的联接与临时副本，只删联接本身不穿透
public static class OpenCleanup
{
    /// <summary>启动清扫短根 / 临时副本 / PDF 预览残留与旧版映射</summary>
    // 启动时还没打开预览，preview 里全是上次强杀的残留，可安全全删
    public static void SweepStartup()
    {
        try
        {
            OpenShortRoot.SweepResidue();
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(OpenCleanup), $"清扫短根残留失败：{ex.Message}");
        }

        try
        {
            TempOpenCopy.SweepResidue();
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(OpenCleanup), $"清扫临时副本残留失败：{ex.Message}");
        }

        try
        {
            Preview.PdfPreviewLoader.CleanupAll();
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(OpenCleanup), $"清理 PDF 预览副本残留失败：{ex.Message}");
        }

        try
        {
            OpenMappingMigration.RemoveLegacyMappings();
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(OpenCleanup), $"迁移旧版盘符映射失败：{ex.Message}");
        }
    }

    /// <summary>退出清理：删除本次联接与临时副本，被占用的下次启动清</summary>
    public static void CleanupAll()
    {
        try
        {
            OpenShortRoot.RemoveAll();
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(OpenCleanup), $"删除短根联接失败：{ex.Message}");
        }

        try
        {
            TempOpenCopy.CleanupAll();
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(OpenCleanup), $"清理临时副本失败：{ex.Message}");
        }

        // 占用中的留到下次启动清扫
        try
        {
            Preview.PdfPreviewLoader.CleanupAll();
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(OpenCleanup), $"清理 PDF 预览临时副本失败：{ex.Message}");
        }
    }
}
