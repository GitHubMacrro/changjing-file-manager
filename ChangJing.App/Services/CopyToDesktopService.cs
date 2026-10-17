namespace ChangJing.App.Services;

/// <summary>「复制到桌面」的接口，由 OperationCoordinator 实现</summary>
public interface ICopyToDesktopService
{
    bool IsImplemented { get; }

    CopyToDesktopResult CopyToDesktop(IReadOnlyList<string> displayPaths);
}

public readonly struct CopyToDesktopResult
{
    private CopyToDesktopResult(bool success, string message)
    {
        Success = success;
        Message = message;
    }

    public bool Success { get; }

    public string Message { get; }

    internal static CopyToDesktopResult Ok(string message) => new(true, message);

    /// <summary>成功受理，供实现本接口的组件复用</summary>
    public static CopyToDesktopResult Succeeded(string message) => new(true, message);

    /// <summary>失败，供实现本接口的组件复用</summary>
    public static CopyToDesktopResult Failed(string message) => new(false, message);
}
