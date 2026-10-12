using System;
using System.Threading;
using OutWit.OS.Native;
using OutWit.OS.Resources;

namespace OutWit.OS.Hardware;

/// <summary>
/// A device's registers through a writable mapping of a region (DEVICE_MEMORY, mapped uncached): each access is one
/// volatile load or store of its width, never merged, split or reordered with the others.
/// </summary>
public sealed unsafe class MmioRegion : IDisposable
{
    #region Fields

    private readonly MemoryMapping m_mapping;

    #endregion

    #region Constructors

    private MmioRegion(MemoryMapping mapping)
    {
        m_mapping = mapping;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Maps a region's memory object writable.
    /// </summary>
    /// <param name="region">The region's memory object, from Device.Memory.</param>
    /// <param name="length">The region's size.</param>
    /// <returns>The registers.</returns>
    /// <exception cref="CapabilityException">The object cannot be mapped writable.</exception>
    public static MmioRegion Map(KernelHandle region, int length) =>
        new(MemoryMapping.Map(region, 0, length, MemoryMapping.READ_WRITE));

    /// <summary>
    /// A full barrier for the device's view of memory: what was written before reaches the device before what follows.
    /// </summary>
    public static void Barrier() => Libwitos.DeviceBarrier();

    /// <summary>
    /// Reads the byte at an offset.
    /// </summary>
    /// <param name="offset">The register's offset.</param>
    /// <returns>Its value.</returns>
    public byte Read8(int offset) => Volatile.Read(ref *At<byte>(offset));

    /// <summary>
    /// Reads the 16-bit register at an offset.
    /// </summary>
    /// <param name="offset">The register's offset, 2-byte aligned.</param>
    /// <returns>Its value.</returns>
    public ushort Read16(int offset) => Volatile.Read(ref *At<ushort>(offset));

    /// <summary>
    /// Reads the 32-bit register at an offset.
    /// </summary>
    /// <param name="offset">The register's offset, 4-byte aligned.</param>
    /// <returns>Its value.</returns>
    public uint Read32(int offset) => Volatile.Read(ref *At<uint>(offset));

    /// <summary>
    /// Writes the byte at an offset.
    /// </summary>
    /// <param name="offset">The register's offset.</param>
    /// <param name="value">The value.</param>
    public void Write8(int offset, byte value) => Volatile.Write(ref *At<byte>(offset), value);

    /// <summary>
    /// Writes the 16-bit register at an offset.
    /// </summary>
    /// <param name="offset">The register's offset, 2-byte aligned.</param>
    /// <param name="value">The value.</param>
    public void Write16(int offset, ushort value) => Volatile.Write(ref *At<ushort>(offset), value);

    /// <summary>
    /// Writes the 32-bit register at an offset.
    /// </summary>
    /// <param name="offset">The register's offset, 4-byte aligned.</param>
    /// <param name="value">The value.</param>
    public void Write32(int offset, uint value) => Volatile.Write(ref *At<uint>(offset), value);

    /// <summary>
    /// Writes the 64-bit register at an offset.
    /// </summary>
    /// <param name="offset">The register's offset, 8-byte aligned.</param>
    /// <param name="value">The value.</param>
    public void Write64(int offset, ulong value) => Volatile.Write(ref *At<ulong>(offset), value);

    private T* At<T>(int offset)
        where T : unmanaged
    {
        if (offset < 0 || offset > m_mapping.Length - sizeof(T) || offset % sizeof(T) != 0)
            throw new ArgumentOutOfRangeException(nameof(offset), $"A {sizeof(T)}-byte register at {offset} lies outside the region.");
        return (T*)(m_mapping.Address + offset);
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        m_mapping.Dispose();
    }

    #endregion

    #region Properties

    /// <summary>
    /// The region's size.
    /// </summary>
    public int Length => m_mapping.Length;

    #endregion
}
