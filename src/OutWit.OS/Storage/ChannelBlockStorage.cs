using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OutWit.OS.Communication;

namespace OutWit.OS.Storage;

/// <summary>
/// A block device a driver serves over its block channel (plan step D3.2): the storage contract on a session of the
/// block protocol. A request goes through the window in transfers of at most the driver's limit and the window; a
/// durable write ends with a transfer the driver makes durable together with the ones before it. The session serves one
/// request at a time, so the resource does too: a request waits for the one before and blocks its thread on the channel.
/// </summary>
public sealed class ChannelBlockStorage : BlockStorageBase
{
    #region Fields

    private readonly IBlockSession m_session;
    private readonly Lock m_lock = new();
    private readonly int m_transfer;

    #endregion

    #region Constructors

    private ChannelBlockStorage(IBlockSession session, BlockReply info)
        : base(new BlockStorageInfo
        {
            Length = info.Length,
            LogicalBlockSize = info.BlockSize,
            OptimalTransferSize = Transfer(session, info),
            IsReadOnly = (info.Flags & BlockMessages.READ_ONLY) != 0,
            SupportsFlush = (info.Flags & BlockMessages.FLUSHES) != 0
        })
    {
        m_session = session;
        m_transfer = Transfer(session, info);
    }

    #endregion

    #region Functions

    /// <summary>
    /// The device a driver serves on its block channel, through a window of WINDOW_BYTES.
    /// </summary>
    /// <param name="channel">The client's end of the block channel; the resource owns it.</param>
    /// <returns>The resource.</returns>
    /// <exception cref="Resources.CapabilityException">The window could not be had or the channel failed.</exception>
    /// <exception cref="IOException">The driver refused the window or its information.</exception>
    public static ChannelBlockStorage Attach(Channel channel) => Open(ChannelBlockSession.Attach(channel));

    /// <summary>
    /// The device a session reaches, as its INFO reply describes it.
    /// </summary>
    /// <param name="session">The session; the resource owns it.</param>
    /// <returns>The resource.</returns>
    /// <exception cref="IOException">The driver refused INFO or described a device the contract cannot hold.</exception>
    public static ChannelBlockStorage Open(IBlockSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        try
        {
            var info = Check(session.Call(new BlockRequest(BlockMessages.INFO)), "INFO", 0, 0);
            if (info.BlockSize <= 0 || info.MaxTransfer < info.BlockSize || Transfer(session, info) < info.BlockSize)
                throw new IOException($"The driver describes blocks of {info.BlockSize} bytes, transfers of {info.MaxTransfer} and a window " +
                    $"of {session.Window.Length}.");
            return new ChannelBlockStorage(session, info);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    protected override ValueTask ReadCoreAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        lock (m_lock)
        {
            for (var done = 0; done < buffer.Length; done += m_transfer)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = Math.Min(m_transfer, buffer.Length - done);
                Check(m_session.Call(new BlockRequest(BlockMessages.READ, 0, offset + done, length)), "READ", offset + done, length);
                m_session.Window[..length].CopyTo(buffer.Span.Slice(done, length));
            }
        }
        return default;
    }

    /// <inheritdoc />
    protected override ValueTask WriteCoreAsync(long offset, ReadOnlyMemory<byte> buffer, WriteOptions options,
        CancellationToken cancellationToken)
    {
        lock (m_lock)
        {
            for (var done = 0; done < buffer.Length; done += m_transfer)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = Math.Min(m_transfer, buffer.Length - done);
                buffer.Span.Slice(done, length).CopyTo(m_session.Window);
                var last = done + length == buffer.Length;
                var flags = last && (options & WriteOptions.Durable) != 0 ? BlockMessages.DURABLE : 0;
                Check(m_session.Call(new BlockRequest(BlockMessages.WRITE, flags, offset + done, length)), "WRITE", offset + done, length);
            }
        }
        return default;
    }

    /// <inheritdoc />
    protected override ValueTask FlushCoreAsync(CancellationToken cancellationToken)
    {
        lock (m_lock)
            Check(m_session.Call(new BlockRequest(BlockMessages.FLUSH)), "FLUSH", 0, 0);
        return default;
    }

    /// <summary>
    /// Gives the block channel up without closing it, the resource ended (D5.1): another client attaches its own window
    /// on it.
    /// </summary>
    /// <returns>The channel, the caller's now.</returns>
    /// <exception cref="InvalidOperationException">The resource's session is not a channel's.</exception>
    public Channel Detach()
    {
        Channel channel;
        lock (m_lock)
            channel = m_session is ChannelBlockSession session ? session.Detach() : throw new InvalidOperationException("The session has no channel.");
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        return channel;
    }

    /// <inheritdoc />
    protected override ValueTask DisposeCoreAsync()
    {
        lock (m_lock)
            m_session.Dispose();
        return default;
    }

    // The bytes of one transfer: the driver's limit within the window, in whole blocks.
    private static int Transfer(IBlockSession session, BlockReply info)
    {
        var bytes = Math.Min(info.MaxTransfer, session.Window.Length);
        return info.BlockSize <= 0 ? 0 : bytes - bytes % info.BlockSize;
    }

    private static BlockReply Check(BlockReply reply, string operation, long offset, long length)
    {
        if (reply.Error != 0)
            throw new IOException($"The driver answered {operation} of {length} bytes at {offset} with errno {reply.Error}.");
        return reply;
    }

    #endregion
}
