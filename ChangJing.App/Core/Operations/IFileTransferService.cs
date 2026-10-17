namespace ChangJing.App.Core.Operations;

// 源与目标都用普通路径，内部转扩展路径
public interface IFileTransferService
{
    bool IsImplemented { get; }

    FileTransferResult Copy(IReadOnlyList<string> sourceDisplayPaths, string targetDisplayPath);

    FileTransferResult Move(IReadOnlyList<string> sourceDisplayPaths, string targetDisplayPath);
}

public readonly struct FileTransferResult
{
    private FileTransferResult(bool started, bool isImplemented, string message)
    {
        Started = started;
        IsImplemented = isImplemented;
        Message = message;
    }

    public bool Started { get; }

    public bool IsImplemented { get; }

    public string Message { get; }

    internal static FileTransferResult NotImplemented(string message)
        => new(false, false, message);

    public static FileTransferResult Accepted(string message)
        => new(true, true, message);

    public static FileTransferResult Rejected(string message)
        => new(false, true, message);
}
