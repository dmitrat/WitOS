using System;
using System.Collections.Generic;
using OutWit.OS.Storage;

namespace OutWit.OS.Tests.Storage;

/// <summary>
/// A block driver in the test's own process: each request goes through the protocol's bytes and the driver's check, then
/// to a disk in memory through a window of its own, as the virtio driver serves its block channel.
/// </summary>
internal sealed class LoopbackBlockSession : IBlockSession
{
    #region Fields

    private readonly MemoryBlockStorage m_disk;
    private readonly byte[] m_window;

    #endregion

    #region Constructors

    public LoopbackBlockSession(MemoryBlockStorage disk, int windowBytes, int maxTransfer, int blockSize = 0)
    {
        m_disk = disk;
        m_window = new byte[windowBytes];
        var flags = (disk.Info.IsReadOnly ? BlockMessages.READ_ONLY : 0) | BlockMessages.FLUSHES;
        Info = new BlockReply(BlockMessages.INFO, 0, disk.Info.Length, blockSize == 0 ? disk.Info.LogicalBlockSize : blockSize, flags,
            maxTransfer);
    }

    #endregion

    #region Functions

    /// <inheritdoc />
    public BlockReply Call(BlockRequest request)
    {
        var served = BlockMessages.DecodeRequest(BlockMessages.Encode(request));
        Requests.Add(served);
        var error = Fail != 0 && served.Operation is BlockMessages.READ or BlockMessages.WRITE
            ? Fail
            : BlockMessages.Check(served, Info, m_window.Length);
        if (error == 0)
        {
            switch (served.Operation)
            {
                case BlockMessages.INFO:
                    return BlockMessages.DecodeReply(BlockMessages.Encode(Info));
                case BlockMessages.READ:
                    m_disk.ReadAsync(served.Offset, m_window.AsMemory((int)served.Window, (int)served.Length)).AsTask().GetAwaiter().GetResult();
                    break;
                case BlockMessages.WRITE:
                    m_disk.WriteAsync(served.Offset, m_window.AsMemory((int)served.Window, (int)served.Length)).AsTask().GetAwaiter().GetResult();
                    break;
                case BlockMessages.FLUSH:
                    Flushes++;
                    break;
            }
        }
        return BlockMessages.DecodeReply(BlockMessages.Encode(new BlockReply(served.Operation, error)));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        IsDisposed = true;
    }

    #endregion

    #region Properties

    /// <inheritdoc />
    public Span<byte> Window => m_window;

    /// <summary>
    /// The INFO reply the driver gives.
    /// </summary>
    public BlockReply Info { get; }

    /// <summary>
    /// The requests served, in order.
    /// </summary>
    public List<BlockRequest> Requests { get; } = [];

    /// <summary>
    /// The errno value every READ and WRITE gets instead of service; zero to serve them.
    /// </summary>
    public int Fail { get; set; }

    /// <summary>
    /// The FLUSH requests served.
    /// </summary>
    public int Flushes { get; private set; }

    /// <summary>
    /// Whether the client ended the session.
    /// </summary>
    public bool IsDisposed { get; private set; }

    #endregion
}
