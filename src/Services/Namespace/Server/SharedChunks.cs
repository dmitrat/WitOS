using System;
using OutWit.OS.Resources;

namespace OutWit.OS.Namespace.Server;

/// <summary>
/// A chunk of a mapped file the service shares (plan step R4.2): the memory the service reads and writes, and the
/// capability a client maps.
/// </summary>
internal interface ISharedChunk : IDisposable
{
    #region Functions

    /// <summary>
    /// A handle of the chunk for a client: MAP and TRANSFER, with WRITE when the client may write it.
    /// </summary>
    /// <param name="writable">Whether the client may map it writable.</param>
    /// <returns>The handle, the client's once it moves.</returns>
    KernelHandle Share(bool writable);

    #endregion

    #region Properties

    /// <summary>
    /// The chunk's bytes, as every mapping of it shows them.
    /// </summary>
    Span<byte> Bytes { get; }

    #endregion
}

/// <summary>
/// Where the service's shared chunks come from: memory objects in the guest, arrays in a host test.
/// </summary>
internal interface IChunkSource
{
    #region Functions

    /// <summary>
    /// A new chunk of zeros.
    /// </summary>
    /// <param name="bytes">Its bytes, whole pages.</param>
    /// <returns>The chunk.</returns>
    ISharedChunk Create(int bytes);

    #endregion
}

/// <summary>
/// Chunks as anonymous memory objects the service maps for itself and duplicates for its clients.
/// </summary>
internal sealed class MemoryChunkSource : IChunkSource
{
    #region Functions

    /// <inheritdoc />
    public ISharedChunk Create(int bytes)
    {
        var memoryObject = MemoryObject.Create(bytes);
        try
        {
            return new MemoryChunk(memoryObject, MemoryMapping.Map(memoryObject, 0, bytes, MemoryMapping.READ_WRITE));
        }
        catch
        {
            memoryObject.Dispose();
            throw;
        }
    }

    #endregion

    #region Nested Types

    private sealed class MemoryChunk(KernelHandle memoryObject, MemoryMapping view) : ISharedChunk
    {
        public Span<byte> Bytes => view.Writable;

        public KernelHandle Share(bool writable) =>
            memoryObject.Duplicate(Rights.Map | Rights.Transfer | (writable ? Rights.Write : 0));

        public void Dispose()
        {
            view.Dispose();
            memoryObject.Dispose(); // the clients' mappings keep the object while they live
        }
    }

    #endregion
}
