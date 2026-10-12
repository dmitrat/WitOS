using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OutWit.OS.FileSystems.Ext4;

/// <summary>
/// The metadata blocks one change of the volume writes (D4.3): each block it touches, copied once from the volume and
/// changed in the copy, which the volume's reads see while the change runs; at the commit the journal writes the copies
/// and then puts them in place. A file's data never enters a transaction: they go to their blocks before the commit,
/// as the journal's ordered mode asks.
/// </summary>
internal sealed class Ext4Transaction
{
    #region Fields

    private readonly Dictionary<ulong, byte[]> m_blocks = [];

    #endregion

    #region Functions

    /// <summary>
    /// A block to change: the transaction's copy, read from the volume the first time.
    /// </summary>
    /// <param name="volume">The volume.</param>
    /// <param name="block">The block.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The copy, which the commit writes.</returns>
    public async ValueTask<byte[]> GetAsync(Ext4Volume volume, ulong block, CancellationToken cancellationToken)
    {
        if (m_blocks.TryGetValue(block, out var bytes))
            return bytes;
        bytes = new byte[volume.Superblock.BlockSize];
        await volume.ReadBlocksAsync(block, bytes, cancellationToken).ConfigureAwait(false);
        m_blocks[block] = bytes;
        return bytes;
    }

    /// <summary>
    /// A block the change writes whole, whatever it held.
    /// </summary>
    /// <param name="block">The block.</param>
    /// <param name="bytes">Its new bytes, one block.</param>
    public void Put(ulong block, byte[] bytes) => m_blocks[block] = bytes;

    /// <summary>
    /// The transaction's copy of a block, if it has one.
    /// </summary>
    /// <param name="block">The block.</param>
    /// <param name="bytes">The copy.</param>
    /// <returns>Whether the transaction holds the block.</returns>
    public bool TryGet(ulong block, out byte[] bytes) => m_blocks.TryGetValue(block, out bytes!);

    /// <summary>
    /// Drops a block a change no longer writes: one it freed after it changed it.
    /// </summary>
    /// <param name="block">The block.</param>
    public void Forget(ulong block) => m_blocks.Remove(block);

    #endregion

    #region Properties

    /// <summary>
    /// The blocks the change writes, in ascending order.
    /// </summary>
    public IReadOnlyList<(ulong Block, byte[] Bytes)> Blocks => [.. m_blocks.OrderBy(pair => pair.Key).Select(pair => (pair.Key, pair.Value))];

    /// <summary>
    /// The blocks the change writes.
    /// </summary>
    public int Count => m_blocks.Count;

    #endregion
}
