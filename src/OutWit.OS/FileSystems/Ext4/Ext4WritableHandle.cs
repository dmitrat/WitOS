using System;
using System.Threading;
using System.Threading.Tasks;

namespace OutWit.OS.FileSystems.Ext4;

/// <summary>
/// An open file of a writable ext mount (D4.3): each request one operation of its filesystem, which reads the inode as
/// it is now and changes it in a transaction of its own.
/// </summary>
internal sealed class Ext4WritableHandle : IFileHandle
{
    #region Fields

    private readonly Ext4FileSystem m_owner;
    private readonly uint m_inode;

    #endregion

    #region Constructors

    public Ext4WritableHandle(Ext4FileSystem owner, uint inode, long length, bool canRead, bool canWrite)
    {
        m_owner = owner;
        m_inode = inode;
        Length = length;
        CanRead = canRead;
        CanWrite = canWrite;
    }

    #endregion

    #region Functions

    /// <inheritdoc />
    public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (!CanRead)
            throw new FileSystemException(FileSystemError.AccessDenied, "The handle does not read.");
        var (read, length) = await m_owner.ReadFileAsync(m_inode, offset, buffer, cancellationToken).ConfigureAwait(false);
        Length = length;
        return read;
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (!CanWrite)
            throw new FileSystemException(FileSystemError.AccessDenied, "The handle does not write.");
        Length = await m_owner.WriteFileAsync(m_inode, offset, buffer, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask SetLengthAsync(long length, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (!CanWrite)
            throw new FileSystemException(FileSystemError.AccessDenied, "The handle does not write.");
        Length = await m_owner.SetFileLengthAsync(m_inode, length, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask FlushAsync(CancellationToken cancellationToken = default) => m_owner.FlushAsync(cancellationToken);

    #endregion

    #region IAsyncDisposable

    /// <inheritdoc />
    public ValueTask DisposeAsync() => default;

    #endregion

    #region Properties

    /// <inheritdoc />
    public long Length { get; private set; }

    /// <inheritdoc />
    public bool CanRead { get; }

    /// <inheritdoc />
    public bool CanWrite { get; }

    #endregion
}
