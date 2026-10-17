using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace ChangJing.App.Core.Shell;

/// <summary>预览处理器的 COM 互操作声明与少量 P/Invoke</summary>
// 处理器由 Shell 注册在 HKCR\<扩展名>\shellex\{8895b1c6-...}，发现后在后台 STA 线程创建 / 驱动
internal static class PreviewHandlerNativeMethods
{
    /// <summary>预览处理器的 shellex 子键名，即 IPreviewHandler 的接口 IID</summary>
    public const string PreviewHandlerShellexKey = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";

    /// <summary>CLSCTX_INPROC_SERVER：进程内 DLL 创建</summary>
    public const uint ClsctxInprocServer = 0x1;

    /// <summary>CLSCTX_LOCAL_SERVER：进程外 prevhost 等 surrogate 创建</summary>
    public const uint ClsctxLocalServer = 0x4;

    /// <summary>COINIT_APARTMENTTHREADED：STA</summary>
    public const uint CoinitApartmentThreaded = 0x2;

    /// <summary>STGM_READ：只读打开</summary>
    public const uint StgmRead = 0x00000000;

    public const int WsChild = 0x40000000;

    public const int WsVisible = 0x10000000;

    public const int WsClipChildren = 0x02000000;

    public const int WsClipSiblings = 0x04000000;

    public const int WmSize = 0x0005;

    /// <summary>进程外预览处理器使用的 surrogate CLSID，即 prevhost.exe</summary>
    public static readonly Guid PrevHostClsid = new("6d2b5079-2f0b-48dd-ab7f-97cec514d30b");

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern void CoUninitialize();

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int CoCreateInstance(
        ref Guid rclsid,
        [MarshalAs(UnmanagedType.IUnknown)] object? pUnkOuter,
        uint dwClsContext,
        ref Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);
}

/// <summary>Win32 RECT</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PreviewRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    /// <summary>由宽高构造，左上角 0,0</summary>
    public static PreviewRect FromSize(int width, int height) => new()
    {
        Left = 0,
        Top = 0,
        Right = Math.Max(0, width),
        Bottom = Math.Max(0, height),
    };
}

/// <summary>Win32 MSG</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PreviewMsg
{
    public IntPtr hwnd;
    public uint message;
    public IntPtr wParam;
    public IntPtr lParam;
    public uint time;
    public int ptX;
    public int ptY;
}

/// <summary>Win32 LOGFONTW，IPreviewHandlerVisuals.SetFont 需要</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct PreviewLogFont
{
    public int lfHeight;
    public int lfWidth;
    public int lfEscapement;
    public int lfOrientation;
    public int lfWeight;
    public byte lfItalic;
    public byte lfUnderline;
    public byte lfStrikeOut;
    public byte lfCharSet;
    public byte lfOutPrecision;
    public byte lfClipPrecision;
    public byte lfQuality;
    public byte lfPitchAndFamily;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string lfFaceName;
}

/// <summary>系统预览处理器接口，IID_IPreviewHandler</summary>
[ComImport]
[Guid("8895b1c6-b41f-4c1c-a562-0d564250836f")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPreviewHandler
{
    void SetWindow(IntPtr hwnd, ref PreviewRect prc);
    void SetRect(ref PreviewRect prc);
    void DoPreview();
    void Unload();
    void SetFocus();
    void QueryFocus(out IntPtr phwnd);

    [PreserveSig]
    uint TranslateAccelerator(ref PreviewMsg pmsg);
}

/// <summary>IInitializeWithFile：用文件路径初始化处理器</summary>
[ComImport]
[Guid("b7d14566-0509-4cce-a71f-0a554233bd9b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithFile
{
    void Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
}

/// <summary>IInitializeWithStream：用流初始化处理器，优先不锁文件</summary>
[ComImport]
[Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithStream
{
    void Initialize(IStream pstream, uint grfMode);
}

/// <summary>IObjectWithSite：部分处理器要求提供 site</summary>
[ComImport]
[Guid("fc4801a3-2ba9-11cf-a229-00aa003d7352")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IObjectWithSite
{
    void SetSite([MarshalAs(UnmanagedType.IUnknown)] object? pUnkSite);

    void GetSite(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppvSite);
}

/// <summary>IPreviewHandlerVisuals：深色主题下设置背景 / 前景色，处理器支持则生效</summary>
[ComImport]
[Guid("196bf9a5-b346-4ef0-aa1e-5dcdb76768b1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPreviewHandlerVisuals
{
    void SetBackgroundColor(uint color);

    void SetFont(ref PreviewLogFont plf);

    void SetTextColor(uint color);
}
