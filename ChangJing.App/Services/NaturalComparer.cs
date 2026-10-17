using System.Runtime.InteropServices;

namespace ChangJing.App.Services;

/// <summary>文件名自然排序：调用 Shlwapi 的 StrCmpLogicalW，与资源管理器一致</summary>
public static class NaturalComparer
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string psz1, string psz2);

    /// <summary>按自然顺序比较两个字符串，null 按空串处理</summary>
    public static int Compare(string? x, string? y)
        => StrCmpLogicalW(x ?? string.Empty, y ?? string.Empty);

    /// <summary>用于排序的 IComparer 实例</summary>
    public static IComparer<string> Instance { get; } = new StringNaturalComparer();

    private sealed class StringNaturalComparer : IComparer<string>
    {
        public int Compare(string? x, string? y) => NaturalComparer.Compare(x, y);
    }
}
