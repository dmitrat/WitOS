using System;
using System.IO;
using OutWit.OS.Resources;

namespace OutWit.OS.Hardware;

/// <summary>
/// A PCI function's MSI-X capability (PCI 3.0 §6.8.2): its table of message entries in one of its BARs, which the driver
/// maps through its device and programs with the address and the data of a message target the kernel bound to an event
/// (<see cref="InterruptBinding.BindMessage"/>, ADR 0029). The kernel programs no device: everything here is the
/// driver's writes through the regions it holds.
/// </summary>
public sealed class MsiX : IDisposable
{
    #region Constants

    /// <summary>
    /// The capability's identifier.
    /// </summary>
    public const byte CAPABILITY = 0x11;

    private const int ENTRY_BYTES = 16;
    private const ushort ENABLE = 0x8000;
    private const ushort FUNCTION_MASK = 0x4000;

    #endregion

    #region Fields

    private readonly MmioRegion m_configuration;
    private readonly int m_capability;
    private readonly KernelHandle m_tableObject;
    private readonly MmioRegion m_table;
    private readonly int m_tableOffset;

    #endregion

    #region Constructors

    private MsiX(MmioRegion configuration, int capability, KernelHandle tableObject, MmioRegion table, int tableOffset, int entries)
    {
        m_configuration = configuration;
        m_capability = capability;
        m_tableObject = tableObject;
        m_table = table;
        m_tableOffset = tableOffset;
        Entries = entries;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Finds the function's MSI-X capability and maps its table.
    /// </summary>
    /// <param name="configuration">The function's configuration space, writable.</param>
    /// <param name="device">The device, with BIND.</param>
    /// <param name="descriptor">Its descriptor.</param>
    /// <returns>The capability; null when the function has none.</returns>
    /// <exception cref="IOException">The table lies outside the BAR's first window or its BAR is not a region.</exception>
    /// <exception cref="CapabilityException">The BAR's region could not be had.</exception>
    public static MsiX? Find(MmioRegion configuration, KernelHandle device, DeviceDescriptor descriptor)
    {
        var capability = PciConfiguration.FindCapability(configuration, CAPABILITY);
        if (capability == 0)
            return null;
        var entries = (configuration.Read16(capability + 2) & 0x7FF) + 1;
        var table = configuration.Read32(capability + 4);
        var region = PciConfiguration.RegionOfBar(configuration, descriptor, (int)(table & 7));
        var offset = (long)(table & ~7U);
        var size = (long)descriptor.Regions[region].Size;
        var window = Math.Min(size, MemoryObject.PAGE * 64L);
        if (offset + (long)entries * ENTRY_BYTES > window)
            throw new IOException($"The MSI-X table of {entries} entries at {offset:x} lies beyond its BAR's first window.");
        var tableObject = Device.Memory(device, region);
        try
        {
            return new MsiX(configuration, capability, tableObject, MmioRegion.Map(tableObject, (int)window), (int)offset, entries);
        }
        catch
        {
            tableObject.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Writes a message into an entry of the table and unmasks it.
    /// </summary>
    /// <param name="entry">The entry, below <see cref="Entries"/>.</param>
    /// <param name="address">The message's address.</param>
    /// <param name="data">The message's data.</param>
    public void Program(int entry, ulong address, uint data)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)entry, (uint)Entries, nameof(entry));
        var at = m_tableOffset + entry * ENTRY_BYTES;
        m_table.Write32(at, (uint)address);
        m_table.Write32(at + 4, (uint)(address >> 32));
        m_table.Write32(at + 8, data);
        m_table.Write32(at + 12, 0); // the entry's mask bit cleared
    }

    /// <summary>
    /// Turns MSI-X on for the function, its function mask cleared: the function sends messages and no INTx from here.
    /// </summary>
    public void Enable()
    {
        var control = m_configuration.Read16(m_capability + 2);
        m_configuration.Write16(m_capability + 2, (ushort)((control | ENABLE) & ~FUNCTION_MASK));
    }

    /// <summary>
    /// Turns MSI-X off for the function.
    /// </summary>
    public void Disable()
    {
        var control = m_configuration.Read16(m_capability + 2);
        m_configuration.Write16(m_capability + 2, (ushort)(control & ~ENABLE));
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        m_table.Dispose();
        m_tableObject.Dispose();
    }

    #endregion

    #region Properties

    /// <summary>
    /// The entries of the table.
    /// </summary>
    public int Entries { get; }

    #endregion
}
