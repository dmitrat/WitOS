using System.Collections.Generic;

namespace OutWit.OS.Hardware;

/// <summary>
/// A device the platform enumerated (witos/device.h, RFC 0007 §13): where it sits on its bus, its identity as the bus
/// reports it, the regions it exposes and the lines it raises. A descriptor says that a device exists; the handle
/// DEVICE_ACQUIRE gives is the authority over it.
/// </summary>
/// <param name="Index">The descriptor's index in the table, which DEVICE_ACQUIRE names.</param>
/// <param name="Bus">How Address and Identity read: PCI.</param>
/// <param name="Address">PCI: bus &lt;&lt; 16 | device &lt;&lt; 8 | function.</param>
/// <param name="Identity">PCI: vendor | device &lt;&lt; 16; class &lt;&lt; 24 | subclass &lt;&lt; 16 | interface &lt;&lt; 8 |
/// revision; subsystem vendor | subsystem &lt;&lt; 16; header type.</param>
/// <param name="Regions">The regions, the configuration page first on PCI.</param>
/// <param name="Lines">The interrupt lines.</param>
public sealed record DeviceDescriptor(int Index, uint Bus, uint Address, IReadOnlyList<uint> Identity,
    IReadOnlyList<DeviceRegion> Regions, IReadOnlyList<DeviceLine> Lines)
{
    #region Constants

    /// <summary>
    /// The PCI bus.
    /// </summary>
    public const uint BUS_PCI = 1;

    #endregion

    #region Properties

    /// <summary>
    /// The descriptor's bytes as the table holds them, which the device manager passes to a driver (witos/driver.h).
    /// </summary>
    public byte[] Bytes { get; init; } = [];

    /// <summary>
    /// The PCI vendor identifier.
    /// </summary>
    public ushort Vendor => (ushort)Identity[0];

    /// <summary>
    /// The PCI device identifier.
    /// </summary>
    public ushort Device => (ushort)(Identity[0] >> 16);

    /// <summary>
    /// The PCI class, subclass and programming interface.
    /// </summary>
    public uint ClassCode => Identity[1] >> 8;

    /// <summary>
    /// The PCI subsystem vendor and subsystem identifiers.
    /// </summary>
    public uint Subsystem => Identity[2];

    #endregion
}
