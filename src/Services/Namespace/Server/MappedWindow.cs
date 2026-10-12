using System;
using OutWit.OS.Resources;

namespace OutWit.OS.Namespace.Server;

/// <summary>
/// A client's window as the service maps it: the duplicate of the client's memory object CONNECT moved, mapped
/// writable.
/// </summary>
internal sealed class MappedWindow : INamespaceWindow, IDisposable
{
    #region Fields

    private readonly KernelHandle m_object;
    private readonly MemoryMapping m_mapping;

    #endregion

    #region Constructors

    private MappedWindow(KernelHandle memoryObject, MemoryMapping mapping)
    {
        m_object = memoryObject;
        m_mapping = mapping;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Maps a client's window.
    /// </summary>
    /// <param name="memoryObject">The window's object, the service's to close.</param>
    /// <param name="bytes">Its bytes, whole pages of at most 64.</param>
    /// <returns>The window.</returns>
    /// <exception cref="CapabilityException">The object cannot be mapped so.</exception>
    public static MappedWindow Map(KernelHandle memoryObject, int bytes)
    {
        try
        {
            return new MappedWindow(memoryObject, MemoryMapping.Map(memoryObject, 0, bytes, MemoryMapping.READ_WRITE));
        }
        catch
        {
            memoryObject.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public byte[] Read(int offset, int count) => m_mapping.Bytes.Slice(offset, count).ToArray();

    /// <inheritdoc />
    public void Write(int offset, ReadOnlySpan<byte> bytes) => bytes.CopyTo(m_mapping.Writable[offset..]);

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        m_mapping.Dispose();
        m_object.Dispose();
    }

    #endregion

    #region Properties

    /// <inheritdoc />
    public int Length => m_mapping.Length;

    #endregion
}
