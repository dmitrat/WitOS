using System;
using System.Threading;
using System.Threading.Tasks;

namespace OutWit.OS.FileSystems.Ext4;

/// <summary>
/// An open file of an ext volume, read through its data map: each run of contiguous blocks is one read of the
/// resource, a hole or an uninitialized extent zeros. Its inode and map were read at the open and never change, so
/// reads may run at once.
/// </summary>
internal sealed class Ext4FileHandle : IFileHandle
{
    #region Fields

    private readonly Ext4Volume m_volume;
    private readonly Ext4DataMap m_map;

    #endregion

    #region Constructors

    public Ext4FileHandle(Ext4Volume volume, Ext4Inode inode, Ext4DataMap map)
    {
        m_volume = volume;
        m_map = map;
        Length = inode.Size;
    }

    #endregion

    #region Functions

    /// <inheritdoc />
    public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset >= Length)
            return 0;
        var total = (int)Math.Min(buffer.Length, Length - offset);
        var size = m_volume.Superblock.BlockSize;
        for (var done = 0; done < total;)
        {
            var position = offset + done;
            var within = (int)(position % size);
            var (physical, blocks) = m_map.Find((ulong)(position / size));
            var bytes = (int)Math.Min((ulong)(total - done), blocks > (ulong)(int.MaxValue / size) ? int.MaxValue : blocks * (ulong)size - (ulong)within);
            var target = buffer.Slice(done, bytes);
            if (physical == 0)
                target.Span.Clear();
            else
                await m_volume.Disk.ReadAsync((long)physical * size + within, target, cancellationToken).ConfigureAwait(false);
            done += bytes;
        }
        return total;
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        throw Ext4FileSystem.ReadOnly();

    /// <inheritdoc />
    public ValueTask SetLengthAsync(long length, CancellationToken cancellationToken = default) => throw Ext4FileSystem.ReadOnly();

    /// <inheritdoc />
    public ValueTask FlushAsync(CancellationToken cancellationToken = default) => default;

    #endregion

    #region IAsyncDisposable

    /// <inheritdoc />
    public ValueTask DisposeAsync() => default;

    #endregion

    #region Properties

    /// <inheritdoc />
    public long Length { get; }

    /// <inheritdoc />
    public bool CanRead => true;

    /// <inheritdoc />
    public bool CanWrite => false;

    #endregion
}
