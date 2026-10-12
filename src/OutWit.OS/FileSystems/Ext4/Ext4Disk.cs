using System;
using System.Threading;
using System.Threading.Tasks;
using OutWit.OS.Storage;

namespace OutWit.OS.FileSystems.Ext4;

/// <summary>
/// The block storage resource an ext4 volume lies on, read by byte ranges: a range the resource's alignment does not
/// cover is read through the aligned range around it, and a range is cut at the resource's transfer limit. A writable
/// mount writes the same way and flushes the resource (D4.3).
/// </summary>
internal sealed class Ext4Disk
{
    #region Fields

    private readonly IBlockStorage m_storage;
    private readonly int m_alignment;
    private readonly int m_transfer;

    #endregion

    #region Constructors

    public Ext4Disk(IBlockStorage storage)
    {
        m_storage = storage;
        m_alignment = storage.Info.RequiredAlignment;
        var limit = storage.Info.MaxTransferSize == 0 ? 1 << 20 : storage.Info.MaxTransferSize;
        m_transfer = Math.Max(m_alignment, limit - limit % m_alignment);
    }

    #endregion

    #region Functions

    /// <summary>
    /// Reads bytes of the resource.
    /// </summary>
    /// <param name="offset">The first byte.</param>
    /// <param name="buffer">What to fill.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>When the bytes are in the buffer.</returns>
    /// <exception cref="FileSystemException">The range lies beyond the resource (Corrupt).</exception>
    public async ValueTask ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (offset < 0 || offset > Length - buffer.Length)
            throw new FileSystemException(FileSystemError.Corrupt, $"The volume names {buffer.Length} bytes at {offset}, beyond its {Length}.");
        if (offset % m_alignment == 0 && buffer.Length % m_alignment == 0)
        {
            for (var done = 0; done < buffer.Length; done += m_transfer)
            {
                var length = Math.Min(m_transfer, buffer.Length - done);
                await m_storage.ReadAsync(offset + done, buffer.Slice(done, length), cancellationToken).ConfigureAwait(false);
            }
            return;
        }
        var start = offset - offset % m_alignment;
        var end = offset + buffer.Length;
        end += (m_alignment - end % m_alignment) % m_alignment;
        var aligned = new byte[end - start];
        await ReadAsync(start, aligned, cancellationToken).ConfigureAwait(false);
        aligned.AsMemory((int)(offset - start), buffer.Length).CopyTo(buffer);
    }

    /// <summary>
    /// Writes bytes of the resource; a range its alignment does not cover is merged into the aligned range around it.
    /// </summary>
    /// <param name="offset">The first byte.</param>
    /// <param name="bytes">The bytes.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>When the bytes are written.</returns>
    public async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        if (offset < 0 || offset > Length - bytes.Length)
            throw new FileSystemException(FileSystemError.Corrupt, $"A write of {bytes.Length} bytes at {offset} lies beyond the volume's {Length}.");
        if (offset % m_alignment == 0 && bytes.Length % m_alignment == 0)
        {
            for (var done = 0; done < bytes.Length; done += m_transfer)
            {
                var length = Math.Min(m_transfer, bytes.Length - done);
                await m_storage.WriteAsync(offset + done, bytes.Slice(done, length), WriteOptions.None, cancellationToken).ConfigureAwait(false);
            }
            return;
        }
        var start = offset - offset % m_alignment;
        var end = offset + bytes.Length;
        end += (m_alignment - end % m_alignment) % m_alignment;
        var aligned = new byte[end - start];
        await ReadAsync(start, aligned, cancellationToken).ConfigureAwait(false);
        bytes.CopyTo(aligned.AsMemory((int)(offset - start)));
        await WriteAsync(start, aligned, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes every completed write durable.
    /// </summary>
    /// <param name="cancellationToken">Cancels the flush.</param>
    /// <returns>When the writes are durable.</returns>
    public ValueTask FlushAsync(CancellationToken cancellationToken) => m_storage.FlushAsync(cancellationToken);

    #endregion

    #region Properties

    /// <summary>
    /// The resource's bytes.
    /// </summary>
    public long Length => m_storage.Info.Length;

    /// <summary>
    /// Whether the resource refuses writes.
    /// </summary>
    public bool IsReadOnly => m_storage.Info.IsReadOnly;

    #endregion
}
