using System;

namespace OutWit.OS.Storage;

/// <summary>
/// A client's session with a block driver (witos/block.h, plan step D3.2): a window both share, through which the
/// device reads and writes, and one request answered at a time.
/// </summary>
public interface IBlockSession : IDisposable
{
    #region Functions

    /// <summary>
    /// Sends a request and waits for its reply.
    /// </summary>
    /// <param name="request">An INFO, READ, WRITE or FLUSH request.</param>
    /// <returns>The driver's reply.</returns>
    /// <exception cref="FormatException">The driver answered with no reply of the request.</exception>
    BlockReply Call(BlockRequest request);

    #endregion

    #region Properties

    /// <summary>
    /// The window: what a READ fills and a WRITE takes.
    /// </summary>
    Span<byte> Window { get; }

    #endregion
}
