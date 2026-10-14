using System;
using System.Threading;
using System.Threading.Tasks;

namespace OutWit.OS.FileSystems.Memory;

/// <summary>
/// An open file of a <see cref="MemoryFileSystem"/> (plan step N4.1): bytes by offset through the volume's lock; the
/// file's bytes live while a handle or a name holds them.
/// </summary>
internal sealed class MemoryFileHandle : IFileHandle
{
    #region Fields

    private readonly MemoryFileSystem m_owner;
    private readonly MemoryFileSystem.Node m_node;
    private int m_disposed;

    #endregion

    #region Constructors

    public MemoryFileHandle(MemoryFileSystem owner, MemoryFileSystem.Node node, bool canRead, bool canWrite)
    {
        m_owner = owner;
        m_node = node;
        CanRead = canRead;
        CanWrite = canWrite;
    }

    #endregion

    #region Functions

    /// <inheritdoc />
    public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(m_disposed != 0, this);
        if (!CanRead)
            throw new FileSystemException(FileSystemError.AccessDenied, "The handle does not read.");
        return m_owner.ReadAsync(m_node, offset, buffer, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(m_disposed != 0, this);
        if (!CanWrite)
            throw new FileSystemException(FileSystemError.AccessDenied, "The handle does not write.");
        await m_owner.WriteAsync(m_node, offset, buffer, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask SetLengthAsync(long length, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(m_disposed != 0, this);
        if (!CanWrite)
            throw new FileSystemException(FileSystemError.AccessDenied, "The handle does not write.");
        await m_owner.SetLengthAsync(m_node, length, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>Nothing is below the memory.</remarks>
    public ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(m_disposed != 0, this);
        return ValueTask.CompletedTask;
    }

    #endregion

    #region IAsyncDisposable

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Interlocked.Exchange(ref m_disposed, 1) == 0 ? m_owner.CloseAsync(m_node) : ValueTask.CompletedTask;

    #endregion

    #region Properties

    /// <inheritdoc />
    public long Length => m_node.Length;

    /// <inheritdoc />
    public bool CanRead { get; }

    /// <inheritdoc />
    public bool CanWrite { get; }

    #endregion
}
