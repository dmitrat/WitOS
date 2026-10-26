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

    /// <summary>
    /// The firmware (plan step C2.1): a device the firmware set up and the boot contract describes, such as its
    /// framebuffer; Address is zero, Identity[0] the kind and the other words by kind.
    /// </summary>
    public const uint BUS_FIRMWARE = 2;

    /// <summary>
    /// The legacy resources of the board that its firmware tables name (ABI-1 1.7, plan step H5.2): Address is zero,
    /// Identity[0] the kind, such as the PC's i8042 (<see cref="LEGACY_I8042"/>).
    /// </summary>
    public const uint BUS_LEGACY = 3;

    /// <summary>
    /// The PC's i8042 keyboard controller on the legacy bus: region 0 its data port, region 1 its status and command port,
    /// line 0 the keyboard's and line 1 the auxiliary device's.
    /// </summary>
    public const uint LEGACY_I8042 = 1;

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
