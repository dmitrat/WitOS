using System;
using System.Collections.Generic;
using OutWit.OS.Resources;

namespace OutWit.OS.Communication;

/// <summary>
/// A message received on a channel: its bytes and the handles that moved to the receiver with it, which are the
/// receiver's to close.
/// </summary>
/// <param name="Bytes">The message's bytes.</param>
/// <param name="Handles">The handles it carried, in their order.</param>
public sealed record ChannelMessage(byte[] Bytes, IReadOnlyList<KernelHandle> Handles) : IDisposable
{
    #region IDisposable

    /// <summary>
    /// Closes the handles nobody took.
    /// </summary>
    public void Dispose()
    {
        foreach (var handle in Handles)
            handle.Dispose();
    }

    #endregion
}
