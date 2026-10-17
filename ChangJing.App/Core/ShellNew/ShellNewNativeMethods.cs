using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ChangJing.App.Core.ShellNew;

/// <summary>ShellNew 枚举用到的 Win32 P/Invoke</summary>
internal static class ShellNewNativeMethods
{
    /// <summary>把 @%SystemRoot%\system32\shell32.dll,-12345 形式解析成本地化文本，返回 HRESULT</summary>
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int SHLoadIndirectString(
        string pszSource,
        StringBuilder pszOutBuf,
        int cchOutBuf,
        IntPtr ppvReserved);

    /// <summary>当前未使用，保留备用</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int ExtractIconEx(
        string lpszFile,
        int nIconIndex,
        IntPtr[]? phiconLarge,
        IntPtr[]? phiconSmall,
        int nIcons);
}
