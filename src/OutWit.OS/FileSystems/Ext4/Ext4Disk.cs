using System;
using System.Threading;
using System.Threading.Tasks;
using OutWit.OS.Storage;

namespace OutWit.OS.FileSystems.Ext4;

/// <summary>
/// The block storage resource an ext4 volume lies on, read by byte ranges: a range the resource's alignment does not
/// cover is read through the aligned range around it, and a range is cut at the resource's transfer limit.
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

    #endregion

    #region Properties

    /// <summary>
    /// The resource's bytes.
    /// </summary>
    public long Length => m_storage.Info.Length;

    #endregion
}
