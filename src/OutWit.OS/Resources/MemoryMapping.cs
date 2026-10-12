using System;
using OutWit.OS.Native;

namespace OutWit.OS.Resources;

/// <summary>
/// A mapping of a memory object into the process, released when disposed.
/// </summary>
public sealed unsafe class MemoryMapping : IDisposable
{
    #region Constants

    /// <summary>
    /// A readable mapping (WIT_MEMORY_READ).
    /// </summary>
    public const uint READ = 1;

    /// <summary>
    /// A readable and writable mapping (WIT_MEMORY_READ | WIT_MEMORY_WRITE).
    /// </summary>
    public const uint READ_WRITE = 3;

    #endregion

    #region Fields

    private void* m_address;

    #endregion

    #region Constructors

    private MemoryMapping(void* address, int length)
    {
        m_address = address;
        Length = length;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Maps bytes of a memory object from a page-aligned offset.
    /// </summary>
    /// <param name="memoryObject">The object, with MAP and the rights the protection needs.</param>
    /// <param name="offset">The page-aligned offset in the object.</param>
    /// <param name="length">The bytes to map; the mapping covers whole pages.</param>
    /// <param name="protection">READ or READ_WRITE.</param>
    /// <returns>The mapping.</returns>
    /// <exception cref="CapabilityException">The handle has not the rights or the window lies outside the object.</exception>
    public static MemoryMapping Map(KernelHandle memoryObject, ulong offset, int length, uint protection)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        void* address = null;
        CapabilityException.ThrowOnError("MEMORY_OBJECT_MAP",
            Libwitos.MemoryMap(memoryObject.Value, offset, (ulong)length, protection, &address));
        return new MemoryMapping(address, length);
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        if (m_address == null)
            return;
        Libwitos.MemoryUnmap(m_address);
        m_address = null;
    }

    #endregion

    #region Properties

    /// <summary>
    /// The mapped bytes.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The mapping was released.</exception>
    public ReadOnlySpan<byte> Bytes
    {
        get
        {
            ObjectDisposedException.ThrowIf(m_address == null, this);
            return new ReadOnlySpan<byte>(m_address, Length);
        }
    }

    /// <summary>
    /// The bytes the mapping was asked for.
    /// </summary>
    public int Length { get; }

    #endregion
}
