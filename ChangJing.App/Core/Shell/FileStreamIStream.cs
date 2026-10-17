using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace ChangJing.App.Core.Shell;

/// <summary>把托管 FileStream 适配成 COM IStream，供 IInitializeWithStream.Initialize 使用</summary>
// 只读流 + FileShare 读写删打开：支持 \\?\ 长路径且不锁文件
internal sealed class FileStreamIStream : IStream
{
    private readonly FileStream _stream;

    public FileStreamIStream(FileStream stream) => _stream = stream;

    /// <inheritdoc/>
    public void Read(byte[] pv, int cb, IntPtr pcbRead)
    {
        int read = _stream.Read(pv, 0, cb);
        if (pcbRead != IntPtr.Zero)
        {
            Marshal.WriteInt32(pcbRead, read);
        }
    }

    /// <inheritdoc/>
    public void Write(byte[] pv, int cb, IntPtr pcbWritten)
    {
        if (pcbWritten != IntPtr.Zero)
        {
            Marshal.WriteInt32(pcbWritten, 0);
        }

        throw new NotSupportedException("预览流为只读。");
    }

    /// <inheritdoc/>
    public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
    {
        SeekOrigin origin = dwOrigin switch
        {
            0 => SeekOrigin.Begin,
            1 => SeekOrigin.Current,
            2 => SeekOrigin.End,
            _ => SeekOrigin.Begin,
        };

        long position = _stream.Seek(dlibMove, origin);
        if (plibNewPosition != IntPtr.Zero)
        {
            Marshal.WriteInt64(plibNewPosition, position);
        }
    }

    /// <inheritdoc/>
    public void SetSize(long libNewSize) => throw new NotSupportedException("预览流为只读。");

    /// <inheritdoc/>
    public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten)
        => throw new NotSupportedException("预览流为只读。");

    /// <inheritdoc/>
    public void Commit(int grfCommitFlags)
    {
    }

    /// <inheritdoc/>
    public void Revert()
    {
    }

    /// <inheritdoc/>
    public void LockRegion(long libOffset, long cb, int dwLockType)
        => throw new NotSupportedException("预览流不支持区域锁定。");

    /// <inheritdoc/>
    public void UnlockRegion(long libOffset, long cb, int dwLockType)
        => throw new NotSupportedException("预览流不支持区域锁定。");

    /// <inheritdoc/>
    public void Stat(out STATSTG pstatstg, int grfStatFlag)
    {
        pstatstg = new STATSTG
        {
            cbSize = _stream.Length,
            type = 2, // STGTY_STREAM
            grfMode = 0,
        };
    }

    /// <inheritdoc/>
    public void Clone(out IStream ppstm) => throw new NotSupportedException("预览流不支持克隆。");
}
