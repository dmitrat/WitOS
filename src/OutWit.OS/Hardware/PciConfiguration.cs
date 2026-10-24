using System.IO;

namespace OutWit.OS.Hardware;

/// <summary>
/// What a driver reads of a PCI function's configuration space through its configuration region (region 0 of its
/// descriptor): the capability list and the BAR a capability names, found among the descriptor's regions by the base
/// the firmware assigned, since the descriptor lists the BARs it sized without their numbers.
/// </summary>
public static class PciConfiguration
{
    #region Constants

    /// <summary>
    /// The command register's offset.
    /// </summary>
    public const int COMMAND = 0x04;

    /// <summary>
    /// The status register's offset; bit 4 says the capability list exists.
    /// </summary>
    public const int STATUS = 0x06;

    /// <summary>
    /// The offset of the first capability's pointer.
    /// </summary>
    public const int CAPABILITIES = 0x34;

    /// <summary>
    /// The offset of BAR 0; the others follow at four bytes each.
    /// </summary>
    public const int BARS = 0x10;

    #endregion

    #region Functions

    /// <summary>
    /// Finds the first capability of an identifier in the function's list.
    /// </summary>
    /// <param name="configuration">The function's configuration space.</param>
    /// <param name="identifier">The capability's identifier: 0x05 MSI, 0x09 vendor, 0x11 MSI-X.</param>
    /// <returns>Its offset; zero when the function has none or no capability list.</returns>
    public static int FindCapability(MmioRegion configuration, byte identifier)
    {
        if ((configuration.Read8(STATUS) & 0x10) == 0)
            return 0;
        var seen = 0;
        for (int at = configuration.Read8(CAPABILITIES) & 0xFC; at != 0 && seen < 48; at = configuration.Read8(at + 1) & 0xFC, seen++)
        {
            if (configuration.Read8(at) == identifier)
                return at;
        }
        return 0;
    }

    /// <summary>
    /// The descriptor's region of a BAR: the region whose base is the address the BAR holds.
    /// </summary>
    /// <param name="configuration">The function's configuration space.</param>
    /// <param name="descriptor">Its descriptor: region 0 its configuration space, the others its BARs.</param>
    /// <param name="bar">The BAR's number, 0 to 5.</param>
    /// <returns>The region's index.</returns>
    /// <exception cref="IOException">No region of the descriptor starts where the BAR points.</exception>
    public static int RegionOfBar(MmioRegion configuration, DeviceDescriptor descriptor, int bar)
    {
        var raw = configuration.Read32(BARS + bar * 4);
        ulong address = raw & 0xFFFFFFF0U;
        if ((raw & 6) == 4)
            address |= (ulong)configuration.Read32(BARS + 4 + bar * 4) << 32;
        for (var region = 1; region < descriptor.Regions.Count; region++)
        {
            if (descriptor.Regions[region].Base == address)
                return region;
        }
        throw new IOException($"BAR {bar} at {address:x} is not among the descriptor's regions.");
    }

    #endregion
}
