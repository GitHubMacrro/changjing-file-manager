using System.Runtime.InteropServices;

namespace ChangJing.App.Core.FileSystem;

/// <summary>「打开方式」相关互操作</summary>
internal static class OpenWithNativeMethods
{
    /// <summary>ASSOC_FILTER_RECOMMENDED：只枚举推荐的处理程序</summary>
    internal const int AssocFilterRecommended = 1;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    internal static extern int SHAssocEnumHandlers(
        string pszExtra,
        int afFilter,
        [MarshalAs(UnmanagedType.Interface)] out IEnumAssocHandlers ppEnumHandler);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    internal static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    /// <summary>与 Win32 IAssocHandler 的 vtable 一致</summary>
    [ComImport]
    [Guid("f04061ac-1659-4a3f-a954-775aa57fc083")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAssocHandler
    {
        [PreserveSig]
        int GetName([MarshalAs(UnmanagedType.LPWStr)] out string ppsz);

        [PreserveSig]
        int GetUIName([MarshalAs(UnmanagedType.LPWStr)] out string ppsz);

        [PreserveSig]
        int GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] out string ppszPath, out int pIndex);

        [PreserveSig]
        int IsRecommended();

        [PreserveSig]
        int MakeDefault([MarshalAs(UnmanagedType.LPWStr)] string pszDescription);

        [PreserveSig]
        int Invoke(IShellItem psi);

        [PreserveSig]
        int CreateInvoker(IShellItem psi, out IntPtr ppInvoker);
    }

    /// <summary>与 Win32 IEnumAssocHandlers 一致，只有 Next</summary>
    [ComImport]
    [Guid("973810ae-9599-4b88-9e4d-6ee98c9552da")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IEnumAssocHandlers
    {
        [PreserveSig]
        int Next(
            uint celt,
            [MarshalAs(UnmanagedType.Interface)] out IAssocHandler rgelt,
            out uint pceltFetched);
    }

    /// <summary>与 Win32 IShellItem 一致的标识，只作参数传递，不调用其方法</summary>
    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
    }
}
