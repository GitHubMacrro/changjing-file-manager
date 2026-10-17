using Microsoft.Win32;

namespace ChangJing.App.Core.Shell;

/// <summary>注册表中发现的系统预览处理器</summary>
public sealed record PreviewHandlerInfo(Guid Clsid, string Extension, string SourceKey);

/// <summary>PreviewHandlerInfo.ProgId 等附加信息</summary>
public static class PreviewHandlerInfoExtensions
{
    /// <summary>该扩展名是否注册了系统预览处理器</summary>
    public static bool HasHandler(this PreviewHandlerInfo? info) => info is not null;
}

/// <summary>某个扩展名的处理器注册信息，含 ProgID 与是否进程外</summary>
public sealed record PreviewHandlerRegistration(Guid Clsid, string? ProgId, string SourceKey);

/// <summary>系统预览处理器发现，走注册表</summary>
public static class PreviewHandlerRegistry
{
    private const string PreviewHandlerShellex = PreviewHandlerNativeMethods.PreviewHandlerShellexKey;

    private const string FileAssociationBase = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts";

    /// <summary>查询某个扩展名的预览处理器注册信息，扩展名含点</summary>
    public static PreviewHandlerRegistration? Find(string extension)
    {
        if (string.IsNullOrEmpty(extension) || extension[0] != '.')
        {
            return null;
        }

        string ext = extension.ToLowerInvariant();

        // 处理器可能注册在扩展名、ProgID 或 SystemFileAssociations 下，依次找
        Guid? clsid = ReadShellexClsid($@"{ext}\shellex\{PreviewHandlerShellex}");
        if (clsid is { } direct)
        {
            return new PreviewHandlerRegistration(direct, ReadProgId(ext), $@"HKCR\{ext}");
        }

        string? progId = ReadProgId(ext);
        if (!string.IsNullOrEmpty(progId))
        {
            clsid = ReadShellexClsid($@"{progId}\shellex\{PreviewHandlerShellex}");
            if (clsid is { } viaProgId)
            {
                return new PreviewHandlerRegistration(viaProgId, progId, $@"HKCR\{progId}");
            }
        }

        clsid = ReadShellexClsid($@"SystemFileAssociations\{ext}\shellex\{PreviewHandlerShellex}");
        if (clsid is { } viaSfa)
        {
            return new PreviewHandlerRegistration(viaSfa, progId, $@"HKCR\SystemFileAssociations\{ext}");
        }

        return null;
    }

    /// <summary>该处理器是否应使用进程外创建，即 CLSCTX_LOCAL_SERVER</summary>
    // 只有 LocalServer32 没 InprocServer32 时必须走进程外 prevhost，否则进程内会失败
    public static bool PreferLocalServer(Guid clsid)
    {
        try
        {
            using RegistryKey? key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{{{clsid:D}}}");
            if (key is null)
            {
                return false;
            }

            using RegistryKey? inproc = key.OpenSubKey("InprocServer32");
            using RegistryKey? local = key.OpenSubKey("LocalServer32");
            using RegistryKey? handler = key.OpenSubKey("InprocHandler32");

            bool hasInproc = inproc is not null;
            bool hasLocal = local is not null || handler is not null;

            // 有 LocalServer32 且没有 InprocServer32 → 进程外
            return hasLocal && !hasInproc;
        }
        catch
        {
            return false;
        }
    }

    private static Guid? ReadShellexClsid(string subKeyPath)
    {
        try
        {
            using RegistryKey? key = Registry.ClassesRoot.OpenSubKey(subKeyPath);
            string? value = key?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return Guid.TryParse(value.Trim(), out Guid clsid) ? clsid : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadProgId(string extension)
    {
        try
        {
            using RegistryKey? key = Registry.ClassesRoot.OpenSubKey(extension);
            return key?.GetValue(null) as string;
        }
        catch
        {
            return null;
        }
    }
}
