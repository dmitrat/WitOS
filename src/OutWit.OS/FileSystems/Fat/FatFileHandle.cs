using System;
using System.Threading;
using System.Threading.Tasks;
using OutWit.Common.Fat.Files;

namespace OutWit.OS.FileSystems.Fat;

/// <summary>
/// An open file of a FAT volume: the library's stream, positioned for each request under the filesystem's lock, so that
/// no position is shared between requests.
/// </summary>
internal sealed class FatFileHandle : IFileHandle
{
    #region Fields

    private readonly FatFileSystem m_owner;
    private readonly FatFileStream m_stream;

    #endregion

    #region Constructors

    public FatFileHandle(FatFileSystem owner, FatFileStream stream)
    {
        m_owner = owner;
        m_stream = stream;
        CanRead = stream.CanRead;
        CanWrite = stream.CanWrite;
    }

    #endregion

    #region Functions

    /// <inheritdoc />
    public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return m_owner.LockedAsync(async () =>
        {
            m_stream.Position = offset;
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await m_stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                total += read;
            }
            return total;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return m_owner.LockedAsync(async () =>
        {
            if (offset > m_stream.Length)
                await m_stream.SetLengthAsync(offset, cancellationToken).ConfigureAwait(false); // the gap reads as zeros
            m_stream.Position = offset;
            await m_stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask SetLengthAsync(long length, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return m_owner.LockedAsync(() => m_stream.SetLengthAsync(length, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask FlushAsync(CancellationToken cancellationToken = default) => m_owner.FlushAsync(cancellationToken); // every writer, then the device

    #endregion

    #region IAsyncDisposable

    /// <summary>
    /// Closes the file; after the filesystem's end, its unmount closed it already.
    /// </summary>
    /// <returns>When the file is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await m_owner.LockedAsync(() => m_stream.DisposeAsync(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    #endregion

    #region Properties

    /// <inheritdoc />
    public long Length => m_stream.Length;

    /// <inheritdoc />
    public bool CanRead { get; }

    /// <inheritdoc />
    public bool CanWrite { get; }

    #endregion
}
