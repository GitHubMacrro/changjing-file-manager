using System.Runtime.InteropServices;
using ChangJing.App.Core.FileSystem;
using ChangJing.App.Core.LongPath;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Core.Operations;

/// <summary>判断两路径是否同卷，决定改名或复制</summary>
// 先比挂载点再比卷序列号，失败按跨卷处理
internal static class TransferVolume
{
    public static bool IsSameVolume(string longPathA, string longPathB)
    {
        string rootA = GetMountRoot(longPathA);
        string rootB = GetMountRoot(longPathB);

        if (rootA.Length > 0 && rootB.Length > 0
            && string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        uint serialA = GetSerialNumber(rootA);
        uint serialB = GetSerialNumber(rootB);
        return serialA != 0 && serialA == serialB;
    }

    /// <summary>取卷挂载点，带尾分隔符，失败为空串</summary>
    public static string GetMountRoot(string longPath)
    {
        try
        {
            string normalized = PathHelper.ToLong(longPath);
            var buffer = new char[512];
            if (NativeMethods.GetVolumePathNameW(normalized, buffer, (uint)buffer.Length))
            {
                int length = Array.IndexOf(buffer, '\0');
                if (length < 0)
                {
                    length = buffer.Length;
                }

                string root = new string(buffer, 0, length);
                if (root.Length > 0)
                {
                    return PathHelper.ToDisplay(root);
                }
            }

            // 某些卷 GetVolumePathNameW 会失败，从路径推导盘根
            return DeriveRoot(normalized);
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(TransferVolume), $"查询卷挂载点失败（{PathHelper.ToDisplay(longPath)}）：{ex.Message}");
            return string.Empty;
        }
    }

    private static string DeriveRoot(string longPath)
    {
        string display;
        try
        {
            display = PathHelper.ToDisplay(longPath);
        }
        catch
        {
            display = longPath;
        }

        if (display.Length >= 2 && char.IsLetter(display[0]) && display[1] == ':')
        {
            return char.ToUpperInvariant(display[0]) + ":\\";
        }

        if (display.Length >= 2 && display[0] == '\\' && display[1] == '\\')
        {
            string[] parts = display.Substring(2).Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                return "\\\\" + parts[0] + "\\" + parts[1];
            }
        }

        return string.Empty;
    }

    private static uint GetSerialNumber(string mountRoot)
    {
        if (string.IsNullOrEmpty(mountRoot))
        {
            return 0;
        }

        try
        {
            string root = mountRoot.EndsWith('\\') ? mountRoot : mountRoot + "\\";
            bool ok = NativeMethods.GetVolumeInformationW(
                root,
                null,
                0,
                out uint serial,
                out _,
                out _,
                null,
                0);
            return ok ? serial : 0;
        }
        catch
        {
            return 0;
        }
    }
}
